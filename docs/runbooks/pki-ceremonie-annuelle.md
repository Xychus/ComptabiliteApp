# Cérémonie de la PKI interne (première installation et renouvellement annuel)

ADR 0010 (révision de la phase 8). Une racine par environnement, hors ligne ; une CA intermédiaire
par année dont la clé privée est une clé KMS non extractible ; certificats finaux de 7 jours.

Acteurs : un **détenteur de la racine** (poste hors ligne, support chiffré) et un **administrateur
AWS** de l'environnement. Rien ici n'est fait par Claude Code.

## 1. Racine (première installation seulement)

Sur le poste hors ligne (OpenSSL 3) :

```bash
openssl ecparam -name secp384r1 -genkey -noout -out racine.key   # reste sur le support chiffré
openssl req -new -x509 -key racine.key -sha384 -days 3650 \
  -subj "/O=<organisation>/CN=Ledger <env> racine" \
  -addext "basicConstraints=critical,CA:TRUE,pathlen:1" \
  -addext "keyUsage=critical,keyCertSign,cRLSign" \
  -addext "subjectKeyIdentifier=hash" -out racine.pem
```

`racine.pem` (public) sera versionné dans `infra/envs/<env>/pki/racine.pem`.

## 2. Nouvelle clé d'intermédiaire

PR : ajouter la génération (ex. `"2026"`) à `generations_intermediaire` de l'environnement ; la
première fois, fixer aussi `versions_images.pki` (image publiée par `images.yml`). Après l'apply,
la sortie `execution.cles_intermediaire` donne l'ARN de la clé.

## 3. Demande de signature (CSR)

L'administrateur invoque la Lambda (mode cérémonie ou non) :

```bash
aws lambda invoke --function-name ledger-<env>-pki \
  --cli-binary-format raw-in-base64-out \
  --payload '{"operation":"demande-signature-intermediaire","cle":"<ARN de la clé>","sujet":"CN=Ledger <env> intermediaire 2026, O=<organisation>"}' \
  reponse.json
jq -r .csr reponse.json > intermediaire.csr
```

La CSR ne contient que la clé publique ; la clé privée reste dans KMS.

## 4. Signature hors ligne

```bash
openssl req -in intermediaire.csr -noout -verify     # signature de la CSR par la clé KMS
cat > ext.cnf <<'EOF'
basicConstraints=critical,CA:TRUE,pathlen:0
keyUsage=critical,keyCertSign,cRLSign
subjectKeyIdentifier=hash
authorityKeyIdentifier=keyid
EOF
openssl x509 -req -in intermediaire.csr -CA racine.pem -CAkey racine.key -sha384 \
  -days 365 -set_serial 0x$(openssl rand -hex 16) -extfile ext.cnf -out intermediaire.pem
openssl verify -CAfile racine.pem intermediaire.pem
```

## 5. Installation

PR : versionner `infra/envs/<env>/pki/intermediaire.pem` (et `racine.pem` la première fois) et
fixer `generation_active`. À l'apply, la Lambda reçoit l'intermédiaire et la rotation des
certificats finaux s'active (`rotate_immediately`) : chaque secret reçoit un certificat en
quelques minutes. Vérifier : `execution.rotation_certificats = true`, aucune erreur de la Lambda,
la métrique `Ledger/Pki` publiée le lendemain.

Les clients ne font confiance qu'à la racine : changer d'intermédiaire ne coupe rien.

## 6. Retrait de l'ancienne génération

Au moins 7 jours après l'installation (échéance du dernier certificat signé par l'ancienne clé),
PR : retirer l'ancienne génération de `generations_intermediaire` ; la clé est programmée pour
suppression (délai `delai_suppression_cles_pki_jours`).

## Alarmes

- `ledger-<env>-pki-echeance-intermediaire` (moins de 30 jours) : planifier la cérémonie.
- `ledger-<env>-pki-echeance-certificats` (moins de 3 jours) : une rotation a échoué ; lire les
  journaux `/aws/lambda/ledger-<env>-pki`, corriger, relancer `aws secretsmanager rotate-secret`.
