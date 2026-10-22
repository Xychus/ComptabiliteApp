# ComptabiliteApp — Grand livre comptable événementiel

Grand livre en partie double, en *event sourcing* + CQRS, conforme aux exigences de conservation
de Revenu Québec (6 ans, intégrité vérifiable). Services en .NET 10, déployés sur AWS
(Aurora PostgreSQL, MSK, ECS Fargate, API Gateway privée), infrastructure en Terraform.

- Règles du projet : [`CLAUDE.md`](CLAUDE.md)
- Plan de mise en œuvre par phases : [`docs/plan/PLAN.md`](docs/plan/PLAN.md)
- Décisions d'architecture : [`docs/adr/`](docs/adr/)

## Prérequis

- SDK .NET 10 (version épinglée dans [`global.json`](global.json))
- Docker, pour les tests d'intégration (Testcontainers)
- PowerShell 7 (`pwsh`), pour le script de seuils de couverture

## Structure

```
src/        services (Command, Projections, Query, Authorizer), Contracts, SharedKernel, Tools
tests/      un projet de tests par projet de src/, plus tests/Architecture et tests/EndToEnd
deploy/     connecteur Debezium (invariant, réutilisé sur MSK Connect) et chaîne locale
infra/      Terraform (à partir de la phase 6)
docs/       plan, ADR, contrat OpenAPI, runbooks
```

Chaque service suit une architecture hexagonale (`Domain` → `Application` → `Infrastructure` →
`Api`/`Worker`), vérifiée par les tests d'architecture ([ADR 0002](docs/adr/0002-architecture-hexagonale-archunitnet.md)).

## Compiler et tester

Les tests s'exécutent avec Microsoft.Testing.Platform (configuré dans `global.json`).

```bash
dotnet build Ledger.slnx -warnaserror

# Tous les tests (nécessite Docker)
dotnet test --solution Ledger.slnx

# Tests rapides, sans Docker
dotnet test --solution Ledger.slnx --filter-not-trait "Category=Integration"

# Tests d'intégration uniquement
dotnet test --solution Ledger.slnx --filter-trait "Category=Integration"
```

Un test d'intégration porte le trait `[Trait("Category", "Integration")]`.

### Couverture

```bash
dotnet test --solution Ledger.slnx --coverage --coverage-output-format cobertura \
  --coverage-settings "$PWD/coverage.config" --results-directory "$PWD/TestResults"
dotnet tool restore
dotnet tool run reportgenerator "-reports:TestResults/**/*.cobertura.xml" "-targetdir:coverage" "-reporttypes:Cobertura;Html"
pwsh eng/Check-Coverage.ps1 -Report coverage/Cobertura.xml
```

Seuils : assemblies `*.Domain` ≥ 90 %, autres ≥ 75 % (le CI échoue en dessous).

## API de commandes

Contrat : [`docs/api/openapi.json`](docs/api/openapi.json), régénéré à chaque build et vérifié par
le CI ([ADR 0014](docs/adr/0014-contrat-http-api-de-commandes.md)). Tout appel exige un jeton JWT
Keycloak (audience `ledger-api`, scope `ledger:write`).

```bash
# Développement, event store en mémoire (refusé hors de l'environnement Development)
ASPNETCORE_ENVIRONMENT=Development \
Ledger__EventStore__Provider=InMemory \
Ledger__Authentication__Authority=https://<keycloak>/realms/<realm> \
dotnet run --project src/Command/Ledger.Command.Api

# Image (tag = version SemVer exacte, jamais « latest »)
docker build -f src/Command/Ledger.Command.Api/Dockerfile -t ledger-command-api:<version> .
```

## API de requête

Contrat : [`docs/api/query-openapi.json`](docs/api/query-openapi.json), régénéré à chaque build et
vérifié par le CI ([ADR 0019](docs/adr/0019-api-de-requete.md)). Tout appel exige un jeton JWT
Keycloak (audience `ledger-api`, scope `ledger:read`). Chaque réponse porte `freshness`
(`throughSequence`, `journalSequence`, `stale`) ; seuil : `Ledger:Fraicheur:RetardMaximal`.

```bash
# Développement, lecture en mémoire vide (refusée hors de l'environnement Development)
ASPNETCORE_ENVIRONMENT=Development Ledger__Lecture__Fournisseur=EnMemoire Ledger__Authentication__Authority=https://<keycloak>/realms/<realm> dotnet run --project src/Query/Ledger.Query.Api

# PostgreSQL : rôle ledger_query (lecture seule des projections et de ledger.events)
ConnectionStrings__Ledger="Host=...;Username=ledger_query;SSL Mode=VerifyFull" Ledger__Authentication__Authority=https://<keycloak>/realms/<realm> dotnet run --project src/Query/Ledger.Query.Api
```

En dehors du développement, la chaîne de connexion `ConnectionStrings__Ledger` (rôle `ledger_app`)
est injectée depuis Secrets Manager. Sondes : `/health/live` et `/health/ready`.

## Chaîne complète en local

`docker compose` démarre PostgreSQL (réplication logique), Kafka, Kafka Connect + Debezium,
Keycloak (realm de test importé), la migration, les deux API et le worker de projections
([ADR 0020](docs/adr/0020-chaine-cdc-debezium.md)) :

```bash
cp .env.example .env          # une valeur par variable ; .env n'est jamais versionné
docker compose up -d --build
deploy/local/demo.sh          # écriture → outbox → Debezium → Kafka → projection → solde
```

Détails, ports et clients Keycloak : [`deploy/local/README.md`](deploy/local/README.md). Le même
assemblage est vérifié par `tests/EndToEnd` (Testcontainers), avec les tests de panne de Kafka et
de redémarrage du worker.

## Versions et contributions

- Versionnage [SemVer 2.0.0](https://semver.org) **par composant** (`command`, `projections`,
  `query`, `authorizer`, `contracts`, `infra`), calculé par release-please à partir des messages
  [Conventional Commits](https://www.conventionalcommits.org/fr/) — voir [ADR 0001](docs/adr/0001-monorepo-semver-par-composant.md).
  Ne jamais modifier une version à la main.
- Une branche et une PR par phase (`phase/NN-nom-court`). Le titre de PR suit Conventional Commits
  (vérifié en CI) et devient le message du commit fusionné (squash).
- Le projet reste en `0.y.z` jusqu'à la mise en production.

## Licence

Voir [`LICENSE`](LICENSE).
