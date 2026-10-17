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
tests/      un projet de tests par projet de src/, plus tests/Architecture
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
