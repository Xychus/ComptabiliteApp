# syntax=docker/dockerfile:1
# Tâche ponctuelle de migration du journal. Contexte de build : racine du dépôt.
#   docker build -f src/Tools/Ledger.Migrations/Dockerfile -t ledger-migrations:<version SemVer exacte> .
# Configuration à l'exécution, jamais dans l'image :
#   LEDGER_MIGRATIONS_CONNECTION_STRING  rôle propriétaire du schéma (secret ECS depuis Secrets Manager)

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo

COPY Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Tools/Ledger.Migrations/Ledger.Migrations.csproj src/Tools/Ledger.Migrations/
RUN dotnet restore src/Tools/Ledger.Migrations/Ledger.Migrations.csproj

COPY src/Tools/Ledger.Migrations/ src/Tools/Ledger.Migrations/
RUN dotnet publish src/Tools/Ledger.Migrations/Ledger.Migrations.csproj \
      --no-restore -c Release -o /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app .
ENV DOTNET_EnableDiagnostics=0
USER 1654
ENTRYPOINT ["dotnet", "Ledger.Migrations.dll"]
