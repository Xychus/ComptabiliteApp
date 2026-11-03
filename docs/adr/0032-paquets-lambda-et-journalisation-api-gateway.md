# 0032 — Paquet zip de l'autorizer dans S3, journalisation d'API Gateway, TLS 1.3 strict

- **Statut** : accepté
- **Date** : 2026-11-02
- **Phase** : 9

## Contexte

L'ADR 0031 retient le runtime géré `dotnet10` pour l'autorizer : son code est un paquet zip, pas
une image. Les images passent par ECR (`images.yml`, ADR 0028) ; rien ne publie encore de zip.
`CLAUDE.md` interdit toute clé gérée par AWS : un paquet Lambda est chiffré par défaut par une
clé détenue par AWS. Les journaux d'accès et d'exécution d'API Gateway exigent un rôle
CloudWatch réglé au niveau du compte et de la région. Enfin, une API REST accepte depuis 2025
des politiques de sécurité TLS « améliorées » et un mode d'accès strict.

## Décision

1. **Bucket des artefacts** par environnement (module `artefacts`,
   `ledger-<env>-artefacts-<compte>-<région>`) : privé, versionné, TLS exigé, SSE-KMS par la CMK
   **`lambda`** seulement, dont l'usage s'étend à S3 (`kms:ViaService` = `lambda` et `s3`) : un
   paquet de déploiement relève du domaine Lambda. Toute écriture exige `If-None-Match: *`
   (condition `s3:if-none-match` de la politique du bucket) : une version publiée ne change jamais.
2. **`images.yml` publie** `authorizer/<version>.zip` (`dotnet publish -r linux-x64`, version de
   `src/Authorizer/version.txt`), et saute une version déjà publiée, comme pour les images.
3. **Terraform désigne la version exacte** (`version_autorizer`, `null` par défaut) : sans paquet
   publié, aucune API Gateway n'est créée et l'équilibreur reste sans entrée. Paquet
   (`source_kms_key_arn`) et variables d'environnement chiffrés par la CMK `lambda`.
4. **Journalisation** : rôle `/ledger/<prefixe>-api-journalisation` (sous la frontière de
   permissions) déclaré comme rôle CloudWatch d'API Gateway du compte (`aws_api_gateway_account`) ;
   groupes d'accès et d'exécution (nom imposé `API-Gateway-Execution-Logs_<id>/<étape>`) créés par
   Terraform avec la CMK `logs`, conservés un an. Niveau d'exécution `ERROR`, sans trace des corps.
5. **TLS 1.3 seulement** sur l'API privée (`SecurityPolicy_TLS13_1_3_2025_09`), mode d'accès
   **`STRICT`** : tous les clients sont internes et .NET 10 ; le nom SNI doit être celui de l'API
   (DNS privé du point de terminaison `execute-api`).
6. **Identités PKI des clients d'exemple** en dev seulement, pour l'essai d'un client réel au point
   d'arrêt ; en prod, une identité par application interne réelle (ADR 0030).

## Conséquences

- Le réglage `aws_api_gateway_account` est unique par compte et par région : un seul
  environnement par compte (déjà le cas, ADR 0011). Le détruire avec dev ne fait que l'oublier.
- Un client qui joint l'API par l'en-tête `x-apigw-api-id` sur le nom du point de terminaison
  (SNI ≠ API) est refusé en mode `STRICT` : il utilise le nom privé
  `<id>.execute-api.<région>.amazonaws.com`.
- La signature des paquets (configuration de signature de code Lambda) arrive avec la signature
  des images en phase 10.
- La clé `lambda` couvre désormais deux usages (variables et paquets) : une compromission de son
  administration touche les deux.

## Options écartées

- **Construire le zip dans `infra-apply`** (`filename`) : le plan des pull requests et le plan
  revu devraient compiler du .NET, et le zip voyagerait avec l'artefact du plan.
- **Nouvelle CMK `artefacts`** : un domaine de plus pour un seul usage, déjà couvert par Lambda.
- **Image conteneur** : écartée par l'ADR 0031 (démarrage à froid sur le chemin des requêtes).
- **Journaux d'accès seulement, sans rôle de compte** : impossible, API Gateway écrit tous ses
  journaux avec ce rôle.
- **`SecurityPolicy_TLS13_1_2_2021_06`** : TLS 1.2 n'a pas de client à servir ici.
