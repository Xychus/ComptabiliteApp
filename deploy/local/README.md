# Chaîne complète en local

Développement uniquement (HTTP sans TLS, un seul nœud par service). Prérequis : Docker, et pour
la démonstration bash, curl et python3.

```bash
cp .env.example .env          # puis donner une valeur à chaque variable
docker compose up -d --build  # PostgreSQL, Kafka, Kafka Connect + Debezium, Keycloak, services .NET
deploy/local/demo.sh          # jeton, plan comptable, période, écriture, puis solde projeté
```

Ordre de démarrage : PostgreSQL → migrations → connexion des rôles (`roles-locaux.sh`) → Kafka
et sujet `ledger.events` (6 partitions) → Kafka Connect → enregistrement du connecteur
(`enregistrer_connecteur.py`) ; Keycloak importe `keycloak/ledger-realm.json`.

| Service | Adresse sur le poste |
|---|---|
| API de commandes | http://localhost:8081 |
| API de requête | http://localhost:8082 |
| Keycloak (realm `ledger`, console `admin`) | http://localhost:8180 |
| Kafka Connect (REST) | http://localhost:8083 |
| Kafka | localhost:9094 |
| PostgreSQL (`postgres`, base `ledger`) | localhost:5433 |

Clients du realm (secret dans `.env`) : `ledger-local` (scopes `ledger:write` et `ledger:read`)
et `ledger-lecture-local` (`ledger:read` seulement). Jeton :

```bash
curl -s -d grant_type=client_credentials -u "ledger-local:$LEDGER_CLIENT_SECRET" \
  http://localhost:8180/realms/ledger/protocol/openid-connect/token
```

Simuler une panne de Kafka : `docker compose stop kafka`, écrire (l'API répond `201`),
`docker compose start kafka` ; les projections rattrapent en quelques secondes.
État du connecteur : `curl -s localhost:8083/connectors/ledger-outbox/status`.

Tout effacer : `docker compose down -v`.
