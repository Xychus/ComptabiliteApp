# syntax=docker/dockerfile:1
# Lambda de la PKI interne (ADR 0010). Contexte de build : racine du dépôt.
#   docker build --provenance=false -f src/Pki/Ledger.Pki.Lambda/Dockerfile -t ledger-pki-lambda:<version SemVer exacte> .
# --provenance=false : Lambda refuse un index d'images (attestations de buildx), il exige un manifeste simple.
# Image « chiseled » (sans shell) plutôt que l'image de base Lambda : l'exécutable est lui-même le
# client de l'API d'exécution (Amazon.Lambda.RuntimeSupport). Configuration à l'exécution :
#   Ledger__Pki__CertificatIntermediaire, Ledger__Pki__CertificatRacine  PEM publics (variables Lambda, CMK « lambda »)
#   Ledger__Pki__CleIntermediaire                                        ARN de la clé KMS de l'intermédiaire
#   Ledger__Pki__Postgres__Hote, __Base, __SecretMaitre                  rotation du mot de passe de Debezium

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo

COPY Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/SharedKernel/Ledger.SharedKernel/Ledger.SharedKernel.csproj src/SharedKernel/Ledger.SharedKernel/
COPY src/Pki/version.txt src/Pki/
COPY src/Pki/Ledger.Pki.Application/Ledger.Pki.Application.csproj src/Pki/Ledger.Pki.Application/
COPY src/Pki/Ledger.Pki.Infrastructure/Ledger.Pki.Infrastructure.csproj src/Pki/Ledger.Pki.Infrastructure/
COPY src/Pki/Ledger.Pki.Lambda/Ledger.Pki.Lambda.csproj src/Pki/Ledger.Pki.Lambda/
RUN dotnet restore src/Pki/Ledger.Pki.Lambda/Ledger.Pki.Lambda.csproj

COPY src/SharedKernel/ src/SharedKernel/
COPY src/Pki/ src/Pki/
RUN dotnet publish src/Pki/Ledger.Pki.Lambda/Ledger.Pki.Lambda.csproj \
      --no-restore -c Release -o /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app .
# Certificats racines RDS (TLS VerifyFull vers Aurora pour la rotation des mots de passe), empreinte épinglée.
ADD --chmod=444 --checksum=sha256:90a6f1a7978a1b1b096dca6c57511ca48d3b8cf052f8e704baf358f34ea2f21b \
    https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem /app/rds-ca-global.pem
ENV DOTNET_EnableDiagnostics=0
USER 1654
ENTRYPOINT ["dotnet", "Ledger.Pki.Lambda.dll"]
