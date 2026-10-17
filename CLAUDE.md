# CLAUDE.md — Grand livre comptable événementiel

Ce fichier est relu au début de chaque session. Il contient les règles non négociables du projet.
Le plan de mise en œuvre est dans `docs/plan/PLAN.md`. Travaille **une phase à la fois**.

## Le projet en une phrase

Grand livre comptable en partie double, en *event sourcing* + CQRS, conforme aux exigences de
conservation de Revenu Québec (6 ans, intégrité vérifiable), déployé sur AWS (Aurora PostgreSQL,
MSK, ECS Fargate, API Gateway privée), services en .NET 10, infrastructure en Terraform.

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
  Query/Ledger.Query.Api/
  Authorizer/Ledger.Authorizer/            # autorizer Lambda (Keycloak)
  Tools/Ledger.Migrations/                 # migrations SQL (DbUp)
  Tools/Ledger.Integrity/                  # vérification hash + balance_after, export archive
tests/                                     # miroir de src/, un projet de tests par projet
infra/
  bootstrap/  modules/  envs/{dev,prod}/  keycloak/
docs/
  plan/PLAN.md  adr/  api/openapi.yaml  runbooks/
.github/workflows/
```

## Règles de dépendances (vérifiées par ArchUnitNET et l'analyse des `.csproj` dans `tests/Architecture`)

- `*.Domain` ne référence que `Ledger.SharedKernel`. Aucun Npgsql, Kafka, AWS, ASP.NET.
- `*.Application` référence `Domain` et définit les **ports** (interfaces).
- `*.Infrastructure` implémente les ports. Seule couche qui touche Npgsql, Kafka, AWS SDK.
- `*.Api` / `*.Worker` = composition (injection de dépendances) + transport. Pas de logique métier.

## Conventions de code (.NET 10)

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
tflint --recursive --chdir infra
checkov -d infra
```

## Sécurité (non négociable)

- Chiffrement au repos avec des **CMK gérées par le client** (une par domaine de données) sur :
  Aurora, MSK, S3, Secrets Manager, SSM, ECR, journaux CloudWatch, stockage éphémère Fargate,
  variables Lambda, état Terraform. Jamais de clé `aws/*` gérée par AWS.
- TLS partout : `rds.force_ssl=1` + Npgsql `SSL Mode=VerifyFull` ; MSK en TLS ; ALB/API Gateway
  TLS 1.2+ ; service à service en TLS via ECS Service Connect ; services AWS via VPC endpoints.
- Aucun secret dans le code, les images, les variables d'environnement en clair ou Git.
  Secrets → Secrets Manager, injectés via `secrets` des task definitions.
- Images : tag = version SemVer exacte, ECR en `IMMUTABLE`. **Jamais `latest`.**
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
