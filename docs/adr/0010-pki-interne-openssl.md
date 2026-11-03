# 0010 — PKI interne : racine OpenSSL hors ligne, intermédiaire en ligne, sans AWS Private CA

- **Statut** : accepté (révisé le 2026-10-25, phase 8 : voir « Révision »)
- **Date** : 2026-10-23
- **Phase** : 6 (décision anticipée ; mise en œuvre en phase 8)

## Contexte

Le transit doit être chiffré par TLS de bout en bout, adossé à une clé que l'organisation
contrôle (point P1 du plan). Les consommateurs de certificats sont : les brokers Kafka et leurs
clients en mTLS (ADR 0024), le trafic de service à service, et le load balancer interne.
AWS Private CA a été écarté par le responsable du projet (2026-10-23) : coût mensuel par CA et
par certificat, et dépendance à AWS contraire à l'objectif d'indépendance du transport. Or le TLS
natif d'ECS Service Connect n'accepte qu'AWS Private CA.

## Décision

1. **Racine** : CA autosignée créée avec OpenSSL, **hors ligne**, détenue par l'organisation
   (clé jamais dans AWS ni dans Git ; support chiffré conservé hors ligne). Durée de 10 ans ;
   elle ne signe que la CA intermédiaire. Clé EC P-384, signature SHA-384.
2. **Intermédiaire** : CA signée par la racine (`pathlen:0`), durée d'un an, clé dans Secrets
   Manager (CMK `secrets`). Renouvelée chaque année par une cérémonie manuelle avec la racine,
   documentée dans un runbook ; le certificat de la racine est publié (non secret) pour les
   magasins de confiance.
3. **Certificats finaux** (brokers Kafka, clients Kafka, services, load balancer) : émis par une
   Lambda de rotation avec l'intermédiaire, **valables 7 jours**, renouvelés avant échéance
   (rotation des secrets au 7 jours, CLAUDE.md). Clé et certificat d'un consommateur stockés
   dans son propre secret, injecté par `secrets` des task definitions. Identité dans le
   `CN` / `SAN` (`projections-worker`, `debezium`, `kafka-1`…).
4. **Révocation** : pas de CRL ni d'OCSP (Kafka ne les vérifie pas) ; la courte durée de vie
   en tient lieu. Une compromission se traite en retirant l'identité des ACL et en renouvelant
   les certificats concernés.
5. **Service à service** : pas de TLS natif de Service Connect (Private CA exigée). Chaque
   service présente lui-même un certificat de la PKI (Kestrel en HTTPS) ; Service Connect ne
   sert qu'à la découverte. Load balancer interne : certificat de la PKI **importé dans ACM**
   (import gratuit), réimporté sous le même ARN à chaque rotation.

## Conséquences

- L'ancre de confiance est sous le contrôle exclusif de l'organisation ; aucune facturation
  par CA ni par certificat.
- Le code de l'émission (Lambda) et la cérémonie annuelle sont à notre charge, avec leurs tests ;
  un échec de rotation non détecté coupe le trafic en 7 jours : alarme sur l'échéance la plus
  proche (phase 11).
- Les tâches ECS clientes redémarrent après chaque rotation (librdkafka, Kafka Connect et
  Kestrel ne relisent pas un secret injecté au démarrage) ; les brokers rechargent leurs
  certificats à chaud.
- Le point P1 est résolu ; le module `pki` de la phase 8 ne crée aucune ressource AWS Private CA.

## Options écartées

- **AWS Private CA (racine ou subordonnée)** : coût, et dépendance à AWS.
- **Certificats autosignés un par un** : chaque client à inscrire dans le magasin de confiance de
  chaque broker, rotation ingérable.
- **TLS natif d'ECS Service Connect** : exige AWS Private CA.
- **Racine en ligne dans Secrets Manager** : une compromission du compte exposerait l'ancre de
  confiance elle-même.

## Révision (2026-10-25, phase 8) — décisions du responsable du projet

Mise en œuvre de la phase 8 ; elle précise ou remplace les points 2, 3 et 5 ci-dessus.

1. **Clé de l'intermédiaire dans KMS, pas dans Secrets Manager** : clé asymétrique
   `ECC_NIST_P384` / `SIGN_VERIFY`, une par génération (cérémonie annuelle). La clé privée n'est
   jamais extractible ; seule la Lambda de la PKI peut signer, et seulement des condensés
   SHA-384 (politique de clé) ; chaque signature est tracée dans CloudTrail. La demande de
   signature (CSR) est produite par la Lambda (`demande-signature-intermediaire`) et signée hors
   ligne par la racine : la clé ne sort jamais d'AWS, la racine ne quitte jamais son support.
2. **Une racine par environnement** : un certificat de dev n'est jamais accepté en prod. Les
   certificats publics (racine, intermédiaire) sont versionnés dans `infra/envs/<env>/pki/`.
3. **Rotation tous les 5 jours** de certificats valables 7 jours : 2 jours de marge pour détecter
   un échec (alarme sous 3 jours, inventaire quotidien `Ledger/Pki`). Protocole de rotation natif
   de Secrets Manager ; le profil d'une identité est porté par les étiquettes de son secret
   (`pki:cn`, `pki:san`, `pki:usage`, `pki:services`, `pki:acm`).
4. **Brokers : redémarrage ordonné, pas de rechargement à chaud** : le rechargement à chaud du
   keystore de l'écouteur contrôleur de KRaft n'est pas documenté. Une sentinelle par nœud
   (`Ledger.Pki.Sentinelle`) arrête la tâche après une rotation, dans le créneau horaire du nœud
   et seulement si la grappe est saine ; ECS relance la tâche, qui installe le nouveau certificat.
   Les autres consommateurs (Debezium, worker, API) sont redéployés par la Lambda ; le certificat
   de l'équilibreur est réimporté dans ACM sous le même ARN.
5. **Mode cérémonie** : tant qu'aucune intermédiaire n'est installée, la Lambda ne fait que
   produire la CSR (et renouveler les mots de passe PostgreSQL, ADR 0029).
6. La Lambda renouvelle aussi le mot de passe PostgreSQL de Debezium (ADR 0029) : même protocole,
   autre type de secret (`rotation:type`).

Conséquences supplémentaires : une clé KMS asymétrique coûte 1 $/mois et chaque signature est
facturée ; KMS n'est pas « indépendant d'AWS », mais Secrets Manager ne l'était pas davantage.
Runbook : `docs/runbooks/pki-ceremonie-annuelle.md`.
