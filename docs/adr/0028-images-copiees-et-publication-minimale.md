# 0028 — Images tierces copiées dans ECR par le CI, publication minimale dès la phase 8

- **Statut** : accepté
- **Date** : 2026-10-25
- **Phase** : 8

## Contexte

Le VPC n'a aucune sortie Internet (ADR 0023) : ECS ne tire que depuis ECR. Le plan prévoyait un
cache *pull-through* ECR pour les images officielles (`apache/kafka` sur Docker Hub, Debezium sur
Quay). Or le cache de Docker Hub exige des identifiants Docker Hub dans un secret Secrets Manager
**chiffré par la clé gérée par AWS** `aws/secretsmanager` (documentation ECR : les clés gérées par
le client ne sont pas prises en charge), ce que `CLAUDE.md` interdit ; ce jeton Docker Hub ne
peut pas non plus être renouvelé tous les 7 jours par nos soins. Par ailleurs, une étiquette
flottante (`3.7`) synchronisée dans un dépôt `IMMUTABLE` serait figée à sa première copie.

Sans image publiée, rien ne démarre en phase 8 : ni la Lambda de la PKI (aucun certificat), ni
Kafka, ni Debezium ; le point d'arrêt de la phase n'aurait presque rien à constater. La
publication complète (signature, attestation, déploiement) est prévue en phase 10.

## Décision

1. Workflow **`images.yml`** (à la demande, environnement GitHub `dev`, rôle `gha-apply`) :
   - nos images (APIs, worker, migrations, Lambda PKI, sentinelle) sont construites et étiquetées
     par la version SemVer exacte de leur composant (`version.txt`, release-please) ;
   - les images officielles tierces (`apache/kafka`, `quay.io/debezium/connect`,
     `public.ecr.aws/aws-observability/aws-otel-collector`) sont **copiées** par
     `docker buildx imagetools create` (même empreinte), étiquette exacte de l'éditeur ;
   - une étiquette déjà présente est sautée (dépôts `IMMUTABLE`).
2. Sur AWS, une image tierce porte **l'étiquette exacte** (ex. Debezium `3.7.0.Final`) de la même
   version mineure que l'étiquette flottante du local et des tests (`3.7`) ; Kafka garde la même
   étiquette exacte partout (`4.3.2`, l'éditeur ne publie pas d'étiquette flottante).
3. Les versions déployées sont des variables Terraform (`versions_images`) ; une version nulle
   laisse le composant sans service.
4. Les migrations suivent **provisoirement** la version du composant `command` (point ouvert de
   la phase 0 : composant release-please de `Ledger.Migrations`).

## Conséquences

- Aucun identifiant de registre tiers dans AWS ; aucune clé gérée par AWS.
- Mettre à jour une image tierce = modifier `images.yml` et `versions_images` dans la même PR.
- La phase 10 reprend `images.yml` dans `deploy.yml` (signature, publication sur étiquette de
  version, prod).

## Options écartées

- **Cache pull-through ECR** : secret Docker Hub chiffré par `aws/secretsmanager` ; étiquettes
  flottantes incompatibles avec `IMMUTABLE`.
- **Sortie Internet (NAT) pour tirer depuis les registres publics** : contraire à l'ADR 0023.
- **Reporter toute la phase 8 au déploiement de la phase 10** : point d'arrêt vide.
