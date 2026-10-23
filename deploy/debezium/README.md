# Connecteur Debezium de l'outbox

`ledger-outbox.json` est la configuration **invariante** du connecteur (ADR 0020) : la même en
local, en test et sur ECS (phase 8, ADR 0024). Elle ne contient aucune valeur propre à un
environnement et ne doit jamais en contenir (vérifié par `ConfigurationConnecteurTests`).

Chaque environnement fournit uniquement ces paramètres, fusionnés sans pouvoir redéfinir une clé
invariante :

| Paramètre | Local (`deploy/local/debezium-local.json`) | AWS (phase 8) |
|---|---|---|
| `database.hostname` | `postgres` | point de terminaison Aurora (écrivain) |
| `database.port` | `5432` | `5432` |
| `database.dbname` | `ledger` | `ledger` |
| `database.user` | `debezium` | `debezium` |
| `database.password` | `${env:LEDGER_DEBEZIUM_PASSWORD}` | référence Secrets Manager |
| `database.sslmode` | `disable` | `verify-full` |
| `database.sslrootcert` | — | certificat racine RDS |

Version : image Debezium `3.7` (tag flottant sur le correctif), la même en local et sur ECS. Le sujet `ledger.events` est créé par
l'infrastructure, pas par le connecteur.

Enregistrement manuel auprès d'un Kafka Connect :

```bash
python3 deploy/local/enregistrer_connecteur.py http://localhost:8083 ledger-outbox \
  deploy/debezium/ledger-outbox.json deploy/local/debezium-local.json
```

Terraform (phase 7) : `merge(jsondecode(file(".../ledger-outbox.json")), local.parametres)` —
jamais `templatefile`, qui interpréterait `${routedByValue}`.
