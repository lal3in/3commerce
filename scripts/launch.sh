#!/usr/bin/env bash
#
# launch.sh — bring the full containerized 3commerce stack up (ADR-0021).
#
#   scripts/launch.sh [--fresh|--reuse] [--env dev|prod]
#
#   --fresh   drop the Postgres volume first -> init SQL re-runs, migrations re-apply,
#             empty catalog: a brand-new deployment.
#   --reuse   keep existing data; just (re)launch (default).
#   --env dev committed dev ES256 keys, admin auto-seeded (default).
#   --env prod a freshly minted keypair is injected and the BL-11 secret gate is enforced.
#
# The catalog is NOT auto-seeded: after a fresh launch, log into the admin Imports screen
# (or POST /api/catalog/admin/import-runs) to load the sample SKUs.
#
# Bare-run dev (scripts/run-all.sh + docker-compose.infra.yml) is unaffected.
set -euo pipefail
cd "$(dirname "$0")/.."

MODE=reuse
ENV=dev
while [ $# -gt 0 ]; do
  case "$1" in
    --fresh) MODE=fresh ;;
    --reuse) MODE=reuse ;;
    --env)   ENV="${2:-}"; shift ;;
    --env=*) ENV="${1#*=}" ;;
    -h|--help) grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown arg: $1" >&2; exit 1 ;;
  esac
  shift
done
[ "$ENV" = dev ] || [ "$ENV" = prod ] || { echo "--env must be dev or prod" >&2; exit 1; }

export ENV
COMPOSE=(-f docker-compose.yml)

if [ "$ENV" = prod ]; then
  command -v openssl >/dev/null 2>&1 || { echo "openssl required for --env prod" >&2; exit 1; }
  echo ">> minting a fresh ES256 keypair (BL-11 gate is enforced in prod)"
  PRIV="$(openssl ecparam -name prime256v1 -genkey -noout 2>/dev/null | openssl pkcs8 -topk8 -nocrypt 2>/dev/null)"
  PUB="$(printf '%s' "$PRIV" | openssl ec -pubout 2>/dev/null)"
  export INTERNAL_AUTH_PRIVATE_KEY="$PRIV"
  export INTERNAL_AUTH_PUBLIC_KEY="$PUB"
  export INTERNAL_AUTH_ADMIN_PASSWORD="$(openssl rand -base64 24 | tr '+/' '-_' | tr -d '=')"
  printf 'ASPNETCORE_ENVIRONMENT=Production\n' > deploy/.env.prod
  COMPOSE+=(-f docker-compose.prod.yml)
fi

DB=(-f docker-compose.db.yml)

if [ "$MODE" = fresh ]; then
  echo ">> FRESH: resetting the external Postgres (init SQL + migrations will re-run)"
  docker compose "${DB[@]}" down -v --remove-orphans || true
  docker compose "${COMPOSE[@]}" down --remove-orphans || true
fi

echo ">> starting the external Postgres instance"
docker compose "${DB[@]}" up -d
for _ in $(seq 1 60); do
  [ "$(docker inspect -f '{{.State.Health.Status}}' 3commerce-postgres 2>/dev/null)" = "healthy" ] && break
  sleep 2
done

echo ">> launching ($MODE, env=$ENV) — building images as needed"
docker compose "${COMPOSE[@]}" up -d --build

cat <<EOF

>> stack up. Endpoints:
   gateway     http://localhost:8080
   storefront  http://localhost:3000
   admin       http://localhost:5200
$([ "$ENV" = prod ] && printf '   admin password: %s\n' "$INTERNAL_AUTH_ADMIN_PASSWORD")
>> catalog is empty on a fresh launch — seed via the admin Imports screen, or:
   curl -X POST http://localhost:8080/api/catalog/admin/import-runs   (admin session required)
>> external Postgres persists across launches; reset it with: docker compose ${DB[*]} down -v
>> tear down app:  docker compose ${COMPOSE[*]} down
EOF
