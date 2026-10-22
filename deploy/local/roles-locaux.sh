#!/bin/sh
# Développement local uniquement : ouvre la connexion des rôles créés NOLOGIN par les migrations
# (ADR 0007). En AWS, LOGIN et mots de passe viennent de Secrets Manager (phase 7).
# Mots de passe lus dans l'environnement (fichier .env, jamais versionné).
set -eu

psql -v ON_ERROR_STOP=1 \
     -v app="$LEDGER_APP_PASSWORD" \
     -v projection="$LEDGER_PROJECTION_PASSWORD" \
     -v query="$LEDGER_QUERY_PASSWORD" \
     -v debezium="$LEDGER_DEBEZIUM_PASSWORD" <<'SQL'
ALTER ROLE ledger_app LOGIN PASSWORD :'app';
ALTER ROLE ledger_projection LOGIN PASSWORD :'projection';
ALTER ROLE ledger_query LOGIN PASSWORD :'query';
ALTER ROLE debezium LOGIN PASSWORD :'debezium';
SQL
