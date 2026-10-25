# syntax=docker/dockerfile:1
# Worker de projections du grand livre. Contexte de build : racine du dépôt.
#   docker build -f src/Projections/Ledger.Projections.Worker/Dockerfile -t ledger-projections-worker:<version SemVer exacte> .
# Jamais de tag « latest » (CLAUDE.md). Configuration à l'exécution, jamais dans l'image :
#   ConnectionStrings__Projections      rôle ledger_projection, SSL Mode=VerifyFull (secret ECS)
#   Ledger__Kafka__BootstrapServers     serveurs d'amorçage Kafka
#   Ledger__Tax__Entities__...          comptes fiscaux par entité (ADR 0018)
# Arguments : aucun (consommation continue) ou « rebuild <projection> » (tâche ponctuelle).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo

COPY Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/SharedKernel/Ledger.SharedKernel/Ledger.SharedKernel.csproj src/SharedKernel/Ledger.SharedKernel/
COPY src/Contracts/version.txt src/Contracts/
COPY src/Contracts/Ledger.Contracts/Ledger.Contracts.csproj src/Contracts/Ledger.Contracts/
COPY src/Postgres/Ledger.Postgres.Infrastructure/Ledger.Postgres.Infrastructure.csproj src/Postgres/Ledger.Postgres.Infrastructure/
COPY src/Projections/version.txt src/Projections/
COPY src/Projections/Ledger.Projections.Application/Ledger.Projections.Application.csproj src/Projections/Ledger.Projections.Application/
COPY src/Projections/Ledger.Projections.Infrastructure/Ledger.Projections.Infrastructure.csproj src/Projections/Ledger.Projections.Infrastructure/
COPY src/Projections/Ledger.Projections.Worker/Ledger.Projections.Worker.csproj src/Projections/Ledger.Projections.Worker/
RUN dotnet restore src/Projections/Ledger.Projections.Worker/Ledger.Projections.Worker.csproj

COPY src/SharedKernel/ src/SharedKernel/
COPY src/Contracts/ src/Contracts/
COPY src/Postgres/ src/Postgres/
COPY src/Projections/ src/Projections/
RUN dotnet publish src/Projections/Ledger.Projections.Worker/Ledger.Projections.Worker.csproj \
      --no-restore -c Release -o /app -p:UseAppHost=false

# Image « chiseled » du runtime (sans ASP.NET) : pas de shell, utilisateur non root (uid 1654).
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app .
# Certificats racines RDS publiés par AWS (TLS VerifyFull vers Aurora, même empreinte que l'image
# des migrations) : la chaîne de connexion désigne Root Certificate=/app/rds-ca-global.pem.
ADD --chmod=444 --checksum=sha256:90a6f1a7978a1b1b096dca6c57511ca48d3b8cf052f8e704baf358f34ea2f21b     https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem /app/rds-ca-global.pem
ENV DOTNET_EnableDiagnostics=0
USER 1654
ENTRYPOINT ["dotnet", "Ledger.Projections.Worker.dll"]
