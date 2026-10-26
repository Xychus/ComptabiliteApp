# syntax=docker/dockerfile:1
# Tâche ponctuelle de migration du journal. Contexte de build : racine du dépôt.
#   docker build -f src/Tools/Ledger.Migrations/Dockerfile -t ledger-migrations:<version SemVer exacte> .
# Configuration à l'exécution, jamais dans l'image :
#   LEDGER_MIGRATIONS_CONNECTION_STRING  chaîne complète (développement local), ou bien :
#   LEDGER_MIGRATIONS_HOTE, _BASE        en clair (définition de tâche ECS)
#   LEDGER_MIGRATIONS_UTILISATEUR, _MOT_DE_PASSE  secrets ECS (secret maître géré par RDS)
# Argument « purger-outbox » : purge planifiée de l'outbox (rôle ledger_maintenance, IAM) :
#   LEDGER_PURGE_HOTE, _BASE, _REGION, _CERTIFICAT_RACINE, _RETENTION_HEURES, _SLOT

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo

COPY Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Postgres/Ledger.Postgres.Infrastructure/Ledger.Postgres.Infrastructure.csproj src/Postgres/Ledger.Postgres.Infrastructure/
COPY src/Tools/Ledger.Migrations/Ledger.Migrations.csproj src/Tools/Ledger.Migrations/
RUN dotnet restore src/Tools/Ledger.Migrations/Ledger.Migrations.csproj

COPY src/Postgres/ src/Postgres/
COPY src/Tools/Ledger.Migrations/ src/Tools/Ledger.Migrations/
RUN dotnet publish src/Tools/Ledger.Migrations/Ledger.Migrations.csproj \
      --no-restore -c Release -o /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app .
# Certificats racines RDS publiés par AWS (TLS VerifyFull vers Aurora). Contenu public, épinglé par
# son empreinte : si AWS publie un nouveau paquet, le build échoue et l'empreinte est mise à jour par PR.
ADD --chmod=444 --checksum=sha256:90a6f1a7978a1b1b096dca6c57511ca48d3b8cf052f8e704baf358f34ea2f21b \
    https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem /app/rds-ca-global.pem
ENV DOTNET_EnableDiagnostics=0 \
    LEDGER_MIGRATIONS_CERTIFICAT_RACINE=/app/rds-ca-global.pem
USER 1654
ENTRYPOINT ["dotnet", "Ledger.Migrations.dll"]
