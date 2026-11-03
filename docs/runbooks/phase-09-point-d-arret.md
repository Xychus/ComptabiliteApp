# Point d'arrêt de la phase 9 : entrée par API Gateway privée en dev

Ordre des opérations. Claude Code n'exécute rien de tout cela : l'administrateur et le pipeline
s'en chargent. Prérequis : la mise en service de la phase 8 (`phase-08-mise-en-service.md`) est
faite (PKI installée, services démarrés, migrations passées, dont `0010`).

## 1. Amorçage et délégation DNS

1. Réappliquer `infra/bootstrap` (administrateur) : zone publique `dev.aws.usinageone.ca`
   (phase 9b) et frontière de permissions sans `acm:ImportCertificate` (phase 9c).
2. Déléguer la zone (procédure de `infra/bootstrap/README.md`) : enregistrement **NS** `dev`
   dans la zone parente, avec les quatre serveurs de `terraform output zone_publique`.
3. Vérifier **avant tout apply** : `dig NS dev.aws.usinageone.ca +short` renvoie les serveurs
   `awsdns`. Sans délégation, ACM ne valide pas le certificat de l'équilibreur et l'apply reste
   bloqué sur `aws_acm_certificate_validation` (jusqu'à 75 minutes) puis échoue.

## 2. Apply en deux temps

1. **Fusion de la PR 9c** → `infra-apply` : bucket des artefacts, certificat ACM public validé,
   équilibreur sans entrée (aucune requête n'y arrive encore), identités PKI des clients
   d'exemple ; secret `ledger-dev/pki/ledger-exemple-*` émis à la rotation suivante (ou
   `aws secretsmanager rotate-secret --secret-id <secret>`). L'ancien certificat importé de
   l'équilibreur et le secret de l'identité `api` sont supprimés.
2. **`images.yml`** : publie `authorizer/0.1.0.zip` dans `ledger-dev-artefacts-<compte>-<région>`
   (récapitulatif du run).
3. **PR « autorizer »** : `version_autorizer = "0.1.0"` dans `infra/envs/dev` → apply : API
   privée, autorizer, VPC Link, plans d'usage, clés en SSM. Sortie `entree` : URL de l'API.

## 3. Essai avec un client réel

Depuis un environnement **AWS CloudShell dans le VPC** (console CloudShell → *Create VPC
environment* : VPC `ledger-dev`, un sous-réseau `applications`, un groupe de sécurité temporaire
créé à la main avec une seule sortie TCP 443 et 8443 vers `10.20.0.0/16`). Le VPC n'a aucune
sortie Internet : seuls `curl` et l'AWS CLI (par les points de terminaison) servent.

```bash
# Certificat client de l'exemple d'écriture (chaîne = certificat + intermédiaire) et racine.
s=$(aws secretsmanager get-secret-value --secret-id ledger-dev/pki/ledger-exemple-ecriture --query SecretString --output text)
echo "$s" | jq -r .chaine > chaine.pem; echo "$s" | jq -r .cle > cle.pem; echo "$s" | jq -r .racine > racine.pem

# Jeton par client credentials, client authentifié par son certificat (aucun secret).
kc=https://keycloak.idp.ledger-dev.internal:8443/realms/ledger
jeton=$(curl -s --cacert racine.pem --cert chaine.pem --key cle.pem \
  -d grant_type=client_credentials -d client_id=ledger-exemple-ecriture \
  "$kc/protocol/openid-connect/token" | jq -r .access_token)

api=$(terraform output -json entree | jq -r .url)   # ou la console : <id>.execute-api.ca-central-1.amazonaws.com/api
```

Le nom de l'API est un nom public d'Amazon : `curl` lui fait confiance sans `--cacert`.
Noter chaque résultat dans le tableau ci-dessous.

| Critère | Commande | Attendu |
|---|---|---|
| Sans jeton | `curl -si "$api/v1/trial-balance"` | `401` |
| Mauvaise audience | jeton d'un autre realm ou modifié | `401` |
| Écriture autorisée | `curl -si -X PUT -H "Authorization: Bearer $jeton" -H 'Content-Type: application/json' -d @compte.json "$api/v1/accounts/<id>"` | `2xx` |
| `actor` = `azp` | `SELECT actor FROM ledger.events ORDER BY global_sequence DESC LIMIT 1;` (tâche de migrations ou psql par IAM) | `ledger-exemple-ecriture` |
| Lecture seule refusée en écriture | même `PUT` avec le jeton de `ledger-exemple-lecture` | `403` |
| Débit dépassé | `for i in $(seq 1 200); do curl -s -o /dev/null -w '%{http_code}\n' -X PUT … ; done \| sort \| uniq -c` | des `429` au-delà de 10 req/s (rafale 20) |
| Hors du VPC | même URL depuis un poste hors VPC | échec de résolution ou `403` |
| Équilibreur direct | `curl -sk https://<nom DNS interne de l'ALB>/health/ready` | délai dépassé (seul le VPC Link entre) |

Journaux : `/aws/apigateway/ledger-dev-api/acces` (champ `clientId`), `/aws/lambda/ledger-dev-api-autorizer`
(raison des refus). Supprimer l'environnement CloudShell et le groupe de sécurité temporaire à
la fin.

## 4. Modifier le realm après le premier démarrage

`--import-realm` ignore un realm existant (ADR 0030). En dev, recréer l'environnement suffit
(`infra-destroy.yml` puis `infra-apply`). Sinon :

1. PR modifiant `infra/keycloak/ledger-realm.json` → apply : nouvelle révision de la définition
   de tâche (le realm y est une variable d'environnement), sans effet sur le realm en base.
2. Arrêter Keycloak : `aws ecs update-service --cluster ledger-dev --service keycloak --desired-count 0`.
   L'autorizer garde sa configuration OIDC en cache ; les nouveaux jetons sont impossibles.
3. Tâche ponctuelle d'import, même définition et même réseau que le service, commande remplacée :
   ```bash
   aws ecs run-task --cluster ledger-dev --launch-type FARGATE --task-definition ledger-dev-keycloak \
     --network-configuration 'awsvpcConfiguration={subnets=[<sous-réseau>],securityGroups=[<groupe keycloak>]}' \
     --overrides '{"containerOverrides":[{"name":"keycloak","command":["mkdir -p /opt/keycloak/data/import && printf %s \"$LEDGER_REALM\" > /opt/keycloak/data/import/ledger-realm.json && printf %s \"$LEDGER_RDS_CA\" > /certificats/rds-ca.pem && exec /opt/keycloak/bin/kc.sh import --file /opt/keycloak/data/import/ledger-realm.json --override true"]}]}'
   ```
   Attendre l'arrêt de la tâche, code de sortie `0` (journal `/ledger-dev/keycloak`).
4. Redémarrer : `--desired-count 1` (2 en prod). Les clés de signature sont régénérées : les jetons
   émis avant l'import sont refusés, les autorizers relisent le JWKS sur le `kid` inconnu.

## 5. Compte d'administration temporaire

Aucun compte permanent (ADR 0030). Pour un diagnostic dans la console d'administration :

1. Arrêter Keycloak (étape 4.2).
2. Tâche ponctuelle `bootstrap-admin`, mot de passe généré pour l'occasion (il apparaît dans la
   description de la tâche : ne jamais le réutiliser) :
   ```bash
   mdp=$(openssl rand -base64 24)
   aws ecs run-task … --overrides "{\"containerOverrides\":[{\"name\":\"keycloak\",\"environment\":[{\"name\":\"KC_BOOTSTRAP_ADMIN_PASSWORD\",\"value\":\"$mdp\"}],\"command\":[\"printf %s \\\"\$LEDGER_RDS_CA\\\" > /certificats/rds-ca.pem && exec /opt/keycloak/bin/kc.sh bootstrap-admin user --username admin-temporaire --password:env KC_BOOTSTRAP_ADMIN_PASSWORD --no-prompt\"]}]}"
   ```
3. Redémarrer Keycloak ; administrer depuis CloudShell dans le VPC (API d'administration,
   realm `master`, client `admin-cli`).
4. **Supprimer le compte** à la fin (`DELETE /admin/realms/master/users/<id>`), vérifier son
   absence, consigner l'intervention (date, raison, personne) dans le journal du plan.

## Constat (à compléter)

| Date | Étape | Résultat | Notes |
|---|---|---|---|
| | délégation DNS | | |
| | certificat ACM émis | | |
| | critères de l'étape 3 | | |
