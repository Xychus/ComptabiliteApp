#!/usr/bin/env bash
# Démonstration de la chaîne locale (docker compose up -d) : jeton Keycloak, plan comptable,
# ouverture de période, écriture, puis lecture du solde projeté (attente bornée sur throughSequence).
# Prérequis : bash, curl, python3. Usage : deploy/local/demo.sh [entité] [montant]
set -euo pipefail

cd "$(dirname "$0")/../.."
set -a; . ./.env; set +a

ENTITE="${1:-DEMO}"
MONTANT="${2:-114.98}"
PERIODE="$(date -u +%Y-%m)"
JOUR="$(date -u +%Y-%m-%d)"
COMMANDES="http://localhost:8081"
REQUETES="http://localhost:8082"

json() { python3 -c "import sys, json; print(json.load(sys.stdin)$1)"; }
uuid7() {
  python3 - <<'PY'
import os, time
b = bytearray(int(time.time() * 1000).to_bytes(6, "big") + os.urandom(10))
b[6] = 0x70 | (b[6] & 0x0F); b[8] = 0x80 | (b[8] & 0x3F)
h = b.hex(); print(f"{h[:8]}-{h[8:12]}-{h[12:16]}-{h[16:20]}-{h[20:]}")
PY
}

JETON="$(curl -fsS -d grant_type=client_credentials -u "ledger-local:${LEDGER_CLIENT_SECRET}" \
  http://localhost:8180/realms/ledger/protocol/openid-connect/token | json '["access_token"]')"
AUTH=(-H "Authorization: Bearer ${JETON}" -H "Content-Type: application/json")

appel() { # méthode url corps : affiche le statut, échoue au-delà de 409
  local statut
  statut="$(curl -sS -o /tmp/ledger-demo.json -w '%{http_code}' -X "$1" "${AUTH[@]}" "$2" ${3:+-d "$3"})"
  echo "$1 $2 → $statut"
  [ "$statut" -lt 400 ] || [ "$statut" = 409 ] || { cat /tmp/ledger-demo.json; exit 1; }
}

appel PUT "$COMMANDES/v1/accounts/1000" "{\"entityId\":\"$ENTITE\",\"name\":\"Encaisse\",\"currency\":\"CAD\",\"normalSide\":\"Debit\",\"isActive\":true,\"noNegativeBalance\":true}"
appel PUT "$COMMANDES/v1/accounts/4000" "{\"entityId\":\"$ENTITE\",\"name\":\"Ventes\",\"currency\":\"CAD\",\"normalSide\":\"Credit\",\"isActive\":true,\"noNegativeBalance\":false}"
appel POST "$COMMANDES/v1/periods/$PERIODE/open" "{\"entityId\":\"$ENTITE\",\"expectedVersion\":0}"
appel PUT "$COMMANDES/v1/journal-entries/$(uuid7)" "{\"entityId\":\"$ENTITE\",\"businessDate\":\"$JOUR\",\"lines\":[{\"accountId\":\"1000\",\"side\":\"Debit\",\"amount\":{\"amount\":\"$MONTANT\",\"currency\":\"CAD\"}},{\"accountId\":\"4000\",\"side\":\"Credit\",\"amount\":{\"amount\":\"$MONTANT\",\"currency\":\"CAD\"}}],\"description\":\"Vente\"}"
SEQUENCE="$(json '["sequenceNumber"]' < /tmp/ledger-demo.json)"

for _ in $(seq 1 60); do
  if curl -fsS "${AUTH[@]}" "$REQUETES/v1/accounts/1000/balance?entityId=$ENTITE" -o /tmp/ledger-demo.json 2>/dev/null \
     && [ "$(json '["freshness"]["throughSequence"]' < /tmp/ledger-demo.json)" -ge "$SEQUENCE" ]; then
    echo "Solde projeté (séquence ≥ $SEQUENCE) :"
    python3 -m json.tool /tmp/ledger-demo.json
    exit 0
  fi
  sleep 1
done
echo "Écriture $SEQUENCE non projetée après 60 s." >&2
exit 1
