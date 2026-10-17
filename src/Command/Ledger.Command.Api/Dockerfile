# syntax=docker/dockerfile:1
# API de commandes du grand livre. Contexte de build : racine du dépôt.
#   docker build -f src/Command/Ledger.Command.Api/Dockerfile -t ledger-command-api:<version SemVer exacte> .
# Jamais de tag « latest » (CLAUDE.md). Configuration à l'exécution, jamais dans l'image :
#   Ledger__Authentication__Authority   URL du realm Keycloak
#   ConnectionStrings__Ledger           rôle ledger_app, SSL Mode=VerifyFull (secret ECS depuis Secrets Manager)
#   OTEL_EXPORTER_OTLP_ENDPOINT         collecteur OpenTelemetry (facultatif)
# Exécution prévue : utilisateur non root, système de fichiers racine en lecture seule
# (docker run --read-only ; readonlyRootFilesystem dans la définition de tâche ECS).

# SDK de la même version majeure que global.json ; global.json n'est pas copié : il fige le SDK
# du poste et du CI (bande 10.0.1xx), dont MCR ne publie pas d'image.
FROM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build
WORKDIR /repo

# Restauration en couche séparée : elle n'est rejouée que si un projet ou une version de paquet change.
COPY Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/SharedKernel/Ledger.SharedKernel/Ledger.SharedKernel.csproj src/SharedKernel/Ledger.SharedKernel/
COPY src/Contracts/version.txt src/Contracts/
COPY src/Contracts/Ledger.Contracts/Ledger.Contracts.csproj src/Contracts/Ledger.Contracts/
COPY src/Command/version.txt src/Command/
COPY src/Command/Ledger.Command.Domain/Ledger.Command.Domain.csproj src/Command/Ledger.Command.Domain/
COPY src/Command/Ledger.Command.Application/Ledger.Command.Application.csproj src/Command/Ledger.Command.Application/
COPY src/Command/Ledger.Command.Infrastructure/Ledger.Command.Infrastructure.csproj src/Command/Ledger.Command.Infrastructure/
COPY src/Command/Ledger.Command.Api/Ledger.Command.Api.csproj src/Command/Ledger.Command.Api/
RUN dotnet restore src/Command/Ledger.Command.Api/Ledger.Command.Api.csproj

COPY src/SharedKernel/ src/SharedKernel/
COPY src/Contracts/ src/Contracts/
COPY src/Command/ src/Command/
# Le contrat OpenAPI est généré et vérifié par le CI, pas dans l'image.
RUN dotnet publish src/Command/Ledger.Command.Api/Ledger.Command.Api.csproj \
      --no-restore -c Release -o /app \
      -p:UseAppHost=false -p:OpenApiGenerateDocuments=false

# Image « chiseled » : sans shell ni gestionnaire de paquets, utilisateur non root « app » (uid 1654).
FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
USER 1654
EXPOSE 8080
ENTRYPOINT ["dotnet", "Ledger.Command.Api.dll"]
