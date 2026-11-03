# CLAUDE.md — Grand livre comptable événementiel

Ce fichier est relu au début de chaque session. Il contient les règles non négociables du projet.
Le plan de mise en œuvre est dans `docs/plan/PLAN.md`. Travaille **une phase à la fois**.

## Le projet en une phrase

Grand livre comptable en partie double, en *event sourcing* + CQRS, conforme aux exigences de
conservation de Revenu Québec (6 ans, intégrité vérifiable), déployé sur AWS (Aurora PostgreSQL,
ECS Fargate, API Gateway privée) avec un Kafka autogéré (image officielle, KRaft), services en .NET 10, infrastructure en Terraform.

## Invariants d'architecture (ne jamais enfreindre)

1. **La table `events` est la seule source de vérité.** Elle est strictement append-only :
   aucun `UPDATE`, aucun `DELETE`, jamais. Une correction = un nouvel événement de contre-passation.
2. **Tout le reste est dérivé et reconstructible** (soldes, balance, projection TPS/TVQ,
   snapshots). Une projection doit pouvoir être vidée et reconstruite par rejeu.
3. **Partie double** : dans chaque écriture, somme des débits = somme des crédits, par devise.
   Validée dans le domaine, sur le contenu de la commande seule.
4. **Aucune décision d'écriture ne lit une projection.** Un invariant transversal (solde non
   négatif) se vérifie à partir du journal (`balance_after` du dernier événement du flux) avec
   concurrence optimiste sur `stream_version` → `409` en cas de conflit.
5. **Le client choisit l'identité, le serveur garde l'autorité** sur l'ordre (`global_sequence`),
   le temps (`recorded_at`), la période et le hash. Ces champs sont ignorés s'ils arrivent en entrée.
6. **Jamais de publication Kafka depuis le code applicatif.** La publication passe par
   l'outbox + Debezium (CDC). Pas de double écriture.
7. **Consommateurs idempotents** : chaque projection tient un checkpoint et ignore tout
   événement déjà appliqué.
8. **Période scellée = immuable.** Toute écriture dans une période scellée est refusée (`409`).

## Structure du monorepo

```
src/
  SharedKernel/Ledger.SharedKernel/        # Money, identifiants, Result, IClock
  Contracts/Ledger.Contracts/              # DTO HTTP + contrats d'événements versionnés
  Command/Ledger.Command.{Domain,Application,Infrastructure,Api}/
  Projections/Ledger.Projections.{Application,Infrastructure,Worker}/
  Query/Ledger.Query.{Application,Infrastructure,Api}/
  Authorizer/Ledger.Authorizer/            # autorizer Lambda (Keycloak)
  Pki/Ledger.Pki.{Application,Infrastructure,Lambda,Sentinelle}/  # PKI interne (ADR 0010)
  Postgres/Ledger.Postgres.Infrastructure/ # source de données Npgsql, authentification IAM (ADR 0029)
  Hebergement/Ledger.Hebergement/          # HTTPS de Kestrel avec un certificat de la PKI
  Tools/Ledger.Migrations/                 # migrations SQL (DbUp)
  Tools/Ledger.Integrity/                  # vérification hash + balance_after, export archive
tests/                                     # miroir de src/, un projet de tests par projet
  EndToEnd/Ledger.EndToEnd.Tests/          # chaîne complète CDC (seule exception au miroir)
deploy/
  debezium/                                # connecteur invariant (réutilisé tel quel sur ECS)
  local/                                   # docker compose : realm Keycloak, rôles, démo
infra/
  bootstrap/  modules/  envs/{dev,prod}/  keycloak/  rds/   # rds/ : certificats racines RDS publics
docs/
  plan/PLAN.md  adr/  api/{openapi,query-openapi}.json  runbooks/
.github/workflows/
```

## Règles de dépendances (vérifiées par ArchUnitNET et l'analyse des `.csproj` dans `tests/Architecture`)

- `*.Domain` ne référence que `Ledger.SharedKernel`. Aucun Npgsql, Kafka, AWS, ASP.NET.
- `*.Application` référence `Domain` et définit les **ports** (interfaces).
- `*.Infrastructure` implémente les ports. Seule couche qui touche Npgsql, Kafka, AWS SDK.
- `*.Api` / `*.Worker` = composition (injection de dépendances) + transport. Pas de logique métier.

## Conventions de code (.NET 10)

- **Langue des identifiants : français, sans accents**, pour tout code nouveau à partir de la
  phase 4b (décision du 2026-10-20) : classes, interfaces, méthodes, propriétés, champs,
  variables, paramètres, constantes, et les nouveaux objets SQL et Terraform. Ex. `SoldeCompte`,
  `IMagasinSoldes`, `periode`, `CalculerSoldeAsync`. Commentaires, messages et noms de tests
  restent en français accentué.
  - Exceptions : conventions .NET (préfixe `I`, suffixe `Async`, `Exception`, `Options`,
    `Tests`), membres imposés par un framework ou une interface existante (`HandleAsync`,
    `ExecuteAsync`), et **contrats externes**, qui restent en anglais : charges et noms des
    événements (le hash en dépend), chemins d'URL et champs JSON de l'API HTTP, attributs AWS.
  - Le code existant (phases 0 à 4a) garde ses noms anglais : on ne le renomme pas en masse.
    On le réutilise tel quel.
- SOLID est une exigence, pas une préférence :
  - une classe = une responsabilité (un gestionnaire de commande, une projection, une règle) ;
  - extension par ajout (`IEntryRule`, `IEventHandler<T>`), pas par `if`/`switch` grandissant ;
  - toute implémentation d'un port passe la suite de tests de contrat de ce port (Liskov) ;
  - interfaces étroites (`IEventAppender` ≠ `IEventReader`) ;
  - le domaine dépend d'abstractions (`IClock`, `IEventStore`), jamais d'implémentations.
- **Argent** : `decimal` en C#, `NUMERIC(20,2)` en base, chaîne décimale en JSON. Jamais `double`/`float`.
  Toujours accompagné de la devise. Arrondi explicite (`MidpointRounding` documenté dans un ADR).
- **Identifiants** : UUIDv7 (`Guid.CreateVersion7()`). Ne jamais trier par UUID pour la comptabilité.
- **Temps** : injecter `IClock` (ou `TimeProvider`). Jamais `DateTime.Now` dans le domaine.
- Erreurs HTTP en Problem Details (RFC 9457). Les erreurs de domaine sont des résultats, pas des exceptions.
- `Nullable` activé, `TreatWarningsAsErrors` activé, analyseurs .NET activés (voir `Directory.Build.props`).
- Gestion centralisée des paquets (`Directory.Packages.props`). Ne pas mettre de version dans un `.csproj`.

## Tests

- Tout code de production arrive avec ses tests dans le même commit.
- Domaine : style Given / When / Then (événements passés → commande → événements attendus ou rejet).
- Invariants universels : tests de propriétés FsCheck (partie double, solde non négatif).
- Ports : suite de contrat abstraite exécutée contre l'implémentation en mémoire ET Testcontainers.
- Intégration : Testcontainers (PostgreSQL, Kafka, Debezium). Pas de dépendance à un AWS réel.
  Tout test d'intégration porte `[Trait("Category", "Integration")]`.
- Seuils de couverture : `Domain` ≥ 90 %, reste ≥ 75 %. Le CI échoue en dessous.

## Commandes

```bash
dotnet build Ledger.slnx -warnaserror
# Les tests utilisent Microsoft.Testing.Platform (global.json) : --solution est obligatoire.
dotnet test --solution Ledger.slnx                                             # nécessite Docker (Testcontainers)
dotnet test --solution Ledger.slnx --filter-not-trait "Category=Integration"   # rapide, sans Docker
terraform -chdir=infra/envs/dev fmt -check -recursive
terraform -chdir=infra/envs/dev init -backend=false && terraform -chdir=infra/envs/dev validate
tflint --init --config "$PWD/infra/.tflint.hcl"
tflint --recursive --chdir infra --config "$PWD/infra/.tflint.hcl"   # sans --config : règles par défaut seulement
checkov -d infra                       # depuis la racine : .checkov.yaml charge les vérifications maison
terraform -chdir=infra/modules/kms init -backend=false && terraform -chdir=infra/modules/kms test   # idem chaque module et bootstrap
```

## Sécurité (non négociable)

- Chiffrement au repos avec des **CMK gérées par le client** (une par domaine de données) sur :
  Aurora, volumes EBS de Kafka, S3, Secrets Manager, SSM, ECR, journaux CloudWatch, stockage éphémère Fargate,
  variables Lambda, état Terraform. Jamais de clé `aws/*` gérée par AWS.
- TLS partout : `rds.force_ssl=1` + Npgsql `SSL Mode=VerifyFull` ; Kafka en **mTLS** (écouteurs
  SSL seulement, ACL par identité de certificat) ; ALB/API Gateway TLS 1.2+ ; service à service en
  TLS (HTTPS par Kestrel) ; services AWS via VPC endpoints.
- **TLS 1.3 privilégié lorsque disponible** : version minimale à TLS 1.3 quand le serveur et tous
  ses clients la prennent en charge, sinon TLS 1.2 au minimum avec 1.3 négocié en priorité
  (politiques de sécurité ALB / API Gateway incluant TLS 1.3, Kestrel, Kafka
  `ssl.enabled.protocols`, `ssl_min_protocol_version` d'Aurora). Tout plancher inférieur à
  TLS 1.3 est justifié en commentaire à côté du paramètre. Jamais en dessous de TLS 1.2.
- PKI interne (ADR 0010) : racine OpenSSL hors ligne détenue par l'organisation, CA intermédiaire
  dans Secrets Manager, certificats finaux de 7 jours. **Jamais d'AWS Private CA** (ni le TLS natif
  d'ECS Service Connect, qui l'exige).
- Aucun secret dans le code, les images, les variables d'environnement en clair ou Git. Rotation des secrets obligatoire au 7 jours
  Secrets → Secrets Manager, injectés via `secrets` des task definitions.
- Images : tag = version SemVer exacte, ECR en `IMMUTABLE`. **Jamais `latest`.**
- Images de base et images tierces (Dockerfiles, `docker-compose.yml`, Testcontainers, CI) :
  - **image officielle du produit uniquement**, publiée par l'éditeur du produit (ex. `postgres`,
    `apache/kafka`, `quay.io/keycloak/keycloak`, `quay.io/debezium/connect`,
    `mcr.microsoft.com/dotnet/*`), jamais une redistribution par un tiers ;
  - **dernière version stable disponible** au moment de l'ajout ou de la mise à jour, avec un
    tag **flottant sur le correctif seulement** : `3.7` suit 3.7.x (ex. Debezium `3.7`, Keycloak
    `26.8`, .NET `10.0`). Pour un produit dont le second chiffre est déjà le correctif
    (PostgreSQL `18.6`), le tag flottant est la version majeure (`18-alpine`). Si l'éditeur ne
    publie pas de tag flottant sur le correctif, tag exact. Jamais `latest` ni un tag qui fait
    changer de version mineure ; jamais de préversion (alpha, bêta, RC, nightly) ;
  - **variante Alpine** lorsque l'éditeur en publie une, **sauf pour les images .NET**, qui
    restent en variante « chiseled » (sans shell ni gestionnaire de paquets, surface plus réduite
    qu'Alpine) ;
  - la même image et la même version partout où le produit est utilisé (compose, tests, CI).
- Aucune ressource publique (pas de `publicly_accessible`, pas de bucket public, API Gateway privée).

## Ce que tu ne dois PAS faire

- Ne jamais exécuter `terraform apply`, `terraform destroy` ni aucune commande qui modifie un
  compte AWS. L'application de l'infrastructure se fait uniquement par le pipeline GitHub Actions.
- Ne jamais modifier un événement stocké ni réécrire une migration déjà fusionnée
  (ajouter une nouvelle migration à la place).
- Ne jamais désactiver un test, un analyseur ou un seuil de couverture pour faire passer le build.
- Ne jamais introduire une dépendance du domaine vers l'infrastructure.
- Ne pas élargir le périmètre d'une phase : si un besoin hors phase apparaît, le noter dans
  la section « Journal » de `docs/plan/PLAN.md`.

## Git, commits et versions

- Une branche par phase : `phase/NN-nom-court`. Une PR par phase (ou sous-étape si elle est grosse).
- Messages en **Conventional Commits** (`feat:`, `fix:`, `refactor:`, `test:`, `docs:`, `ci:`,
  `chore:`), avec portée = composant (`feat(command): …`). Changement incompatible : `feat!:` ou
  pied `BREAKING CHANGE:`.
- Versionnage SemVer 2.0.0 par composant, calculé par release-please (ne jamais éditer une version
  à la main). Le projet est en `0.y.z` jusqu'à la mise en production.
- Le préfixe d'URL `/v1` et `schema_version` des événements ne sont PAS la version SemVer du service.
- Les événements ne cassent jamais : un changement de schéma passe par l'upcasting.

## Définition de « terminé » pour une tâche

1. Le code compile sans avertissement.
2. Les tests (y compris d'architecture) passent localement.
3. Les critères d'acceptation de la phase dans `PLAN.md` sont vérifiés.
4. Les cases de la phase sont cochées dans `PLAN.md` et le journal est mis à jour.
5. Toute décision structurante est consignée dans un ADR (`docs/adr/NNNN-titre.md`).
