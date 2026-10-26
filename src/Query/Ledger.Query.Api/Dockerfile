# syntax=docker/dockerfile:1
# API de requête du grand livre. Contexte de build : racine du dépôt.
#   docker build -f src/Query/Ledger.Query.Api/Dockerfile -t ledger-query-api:<version SemVer exacte> .
# Jamais de tag « latest » (CLAUDE.md). Configuration à l'exécution, jamais dans l'image :
#   Ledger__Authentication__Authority   URL du realm Keycloak
#   ConnectionStrings__Ledger           rôle ledger_query, SSL Mode=VerifyFull (secret ECS depuis Secrets Manager)
#   OTEL_EXPORTER_OTLP_ENDPOINT         collecteur OpenTelemetry (facultatif)
# Exécution prévue : utilisateur non root, système de fichiers racine en lecture seule.

# Même SDK que l'image de l'API de commandes (voir son Dockerfile).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo

COPY Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/SharedKernel/Ledger.SharedKernel/Ledger.SharedKernel.csproj src/SharedKernel/Ledger.SharedKernel/
COPY src/Contracts/version.txt src/Contracts/
COPY src/Contracts/Ledger.Contracts/Ledger.Contracts.csproj src/Contracts/Ledger.Contracts/
COPY src/Postgres/Ledger.Postgres.Infrastructure/Ledger.Postgres.Infrastructure.csproj src/Postgres/Ledger.Postgres.Infrastructure/
COPY src/Hebergement/Ledger.Hebergement/Ledger.Hebergement.csproj src/Hebergement/Ledger.Hebergement/
COPY src/Query/version.txt src/Query/
COPY src/Query/Ledger.Query.Application/Ledger.Query.Application.csproj src/Query/Ledger.Query.Application/
COPY src/Query/Ledger.Query.Infrastructure/Ledger.Query.Infrastructure.csproj src/Query/Ledger.Query.Infrastructure/
COPY src/Query/Ledger.Query.Api/Ledger.Query.Api.csproj src/Query/Ledger.Query.Api/
RUN dotnet restore src/Query/Ledger.Query.Api/Ledger.Query.Api.csproj

COPY src/SharedKernel/ src/SharedKernel/
COPY src/Contracts/ src/Contracts/
COPY src/Postgres/ src/Postgres/
COPY src/Hebergement/ src/Hebergement/
COPY src/Query/ src/Query/
# Le contrat OpenAPI est généré et vérifié par le CI, pas dans l'image.
RUN dotnet publish src/Query/Ledger.Query.Api/Ledger.Query.Api.csproj \
      --no-restore -c Release -o /app \
      -p:UseAppHost=false -p:OpenApiGenerateDocuments=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app .
# Certificats racines RDS publiés par AWS (TLS VerifyFull vers Aurora, même empreinte que l'image
# des migrations) : la chaîne de connexion désigne Root Certificate=/app/rds-ca-global.pem.
ADD --chmod=444 --checksum=sha256:90a6f1a7978a1b1b096dca6c57511ca48d3b8cf052f8e704baf358f34ea2f21b \
    https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem /app/rds-ca-global.pem
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
USER 1654
# 8080 : HTTP en local ; 8443 : HTTPS avec le certificat de la PKI interne (Ledger__Tls__Chaine / __Cle, ADR 0010).
EXPOSE 8080 8443
ENTRYPOINT ["dotnet", "Ledger.Query.Api.dll"]
