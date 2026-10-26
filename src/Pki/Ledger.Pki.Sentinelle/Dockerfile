# syntax=docker/dockerfile:1
# Sentinelle des brokers Kafka (ADR 0024 révisée). Contexte de build : racine du dépôt.
#   docker build -f src/Pki/Ledger.Pki.Sentinelle/Dockerfile -t ledger-pki-sentinelle:<version SemVer exacte> .
# Deux conteneurs de la tâche d'un broker utilisent cette image :
#   « installer » : écrit keystore.pem et truststore.pem dans le volume partagé, puis s'arrête ;
#   sans argument : surveille le secret et arrête la tâche quand un redémarrage est sûr.
# La définition de tâche impose l'utilisateur 1000 (celui du broker) : les fichiers sont en 0600.
# Configuration à l'exécution : Ledger__Sentinelle__Secret, __Dossier, __Serveurs, __NombreNoeuds, __Creneau.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo

COPY Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/SharedKernel/Ledger.SharedKernel/Ledger.SharedKernel.csproj src/SharedKernel/Ledger.SharedKernel/
COPY src/Pki/version.txt src/Pki/
COPY src/Pki/Ledger.Pki.Application/Ledger.Pki.Application.csproj src/Pki/Ledger.Pki.Application/
COPY src/Pki/Ledger.Pki.Infrastructure/Ledger.Pki.Infrastructure.csproj src/Pki/Ledger.Pki.Infrastructure/
COPY src/Pki/Ledger.Pki.Sentinelle/Ledger.Pki.Sentinelle.csproj src/Pki/Ledger.Pki.Sentinelle/
RUN dotnet restore src/Pki/Ledger.Pki.Sentinelle/Ledger.Pki.Sentinelle.csproj

COPY src/SharedKernel/ src/SharedKernel/
COPY src/Pki/ src/Pki/
RUN dotnet publish src/Pki/Ledger.Pki.Sentinelle/Ledger.Pki.Sentinelle.csproj \
      --no-restore -c Release -o /app -p:UseAppHost=false

# Point de montage du volume partagé de la tâche : ECS copie dans le volume le répertoire déclaré
# par VOLUME, avec ses permissions (sinon root:root 0755, interdit en écriture à un conteneur non
# root). 1777 comme /tmp : chaque conteneur écrit sous son uid (1000 Kafka, 1001 Debezium), les
# fichiers restent en 0600, le bit collant interdit de supprimer ceux d'un autre.
RUN mkdir -p /volumes/certificats && chmod 1777 /volumes/certificats

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app .
COPY --from=build /volumes/ /
VOLUME /certificats
ENV DOTNET_EnableDiagnostics=0
USER 1654
ENTRYPOINT ["dotnet", "Ledger.Pki.Sentinelle.dll"]
