#!/usr/bin/env bash
# Bring up the FULL local env the light way (ADR-0009 bare-run): only Postgres + RabbitMQ in Docker,
# everything else as host processes — so it never triggers the 13-image build that OOMs a small Docker VM.
# Usage: scripts/dev-up.sh [--fresh] [--with-frontends] [--seed] [--dummy-data|--data empty|catalog|smoke|dummy|full|exhaustive|mirror-prod]
#   --fresh  Wipe the local DB volume first (dev-down --clean), so you always get a truly clean start:
#            empty Postgres -> latest migrations -> fresh seed, on latest-built code. Use it whenever
#            state has drifted (e.g. a demo run mutated data) or you just want a guaranteed-clean env.
# Maintain: services + migrations derive from lib/services.sh (auto) — nothing per-service to edit here.
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/lib/preflight.sh
source scripts/lib/services.sh
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH:$DOTNET_ROOT/tools"

WITH_FRONTENDS=0; SEED=0; DATA_PROFILE="empty"; FRESH=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --fresh) FRESH=1; shift ;;
    --with-frontends) WITH_FRONTENDS=1; shift ;;
    --seed) SEED=1; DATA_PROFILE="catalog"; shift ;;
    --dummy-data) DATA_PROFILE="dummy"; shift ;;
    --data) DATA_PROFILE="$2"; shift 2 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

require_docker

if (( FRESH )); then
  # Truly clean start: stop anything running and DROP the Postgres data volume so the DB is recreated
  # empty (init-databases.sql reruns) — otherwise stale/mutated data survives across restarts because
  # `docker compose down` (without -v) keeps the named volume.
  echo "== 0/4 fresh: wiping local stack + DB volume =="
  scripts/dev-down.sh --clean >/dev/null 2>&1 || true
fi

echo "== 1/4 infra (Postgres + RabbitMQ + pgAdmin) =="
docker compose -f docker-compose.infra.yml up -d
for _ in $(seq 1 60); do docker exec 3commerce-postgres pg_isready -U postgres >/dev/null 2>&1 && break; sleep 2; done
echo "  pgAdmin (all 14 DBs): http://localhost:5480  (admin@3commerce.dev / pgadmin_dev)"

echo "== 2/4 build + migrate =="
dotnet build 3commerce.sln >/dev/null
for s in $(ef_projects); do
  dotnet ef database update -p "src/Services/$s/Infrastructure" -s "src/Services/$s/Api" --no-build >/dev/null 2>&1 \
    && echo "  migrated $s" || echo "  WARN migrate $s failed (see logs)"
done

echo "== 3/4 services (bare-run) =="
scripts/run-all.sh start

if (( WITH_FRONTENDS )); then
  echo "== frontends =="
  ( cd src/Storefront && GATEWAY_URL=http://localhost:8080 npm run dev >/tmp/3c-storefront.log 2>&1 & )
  ( ASPNETCORE_URLS="http://localhost:5200" ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Admin --no-build >/tmp/3c-admin.log 2>&1 & )
  ( ASPNETCORE_URLS="http://localhost:5300" ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/SupplierPortal --no-build >/tmp/3c-supplier-portal.log 2>&1 & )
  echo "  storefront :3000 + admin :5200 + supplier portal :5300 starting"
fi

echo "== 4/4 health =="
sleep 12
for entry in "${SERVICES[@]}"; do
  name="${entry%%:*}"; port="${entry##*:}"
  printf '  %-12s %s\n' "$name" "$(curl -s -o /dev/null -w '%{http_code}' -m 4 "http://localhost:$port/health/ready" 2>/dev/null)"
done
echo "  gateway      $(curl -s -o /dev/null -w '%{http_code}' -m 4 http://localhost:8080/health 2>/dev/null)"

case "$DATA_PROFILE" in
  empty)
    ;;
  catalog)
    j=$(mktemp)
    curl -s -c "$j" -X POST http://localhost:8080/api/identity/login -H 'content-type: application/json' -d '{"email":"admin@3commerce.local","password":"dev-admin-password-1"}' -o /dev/null
    curl -s -b "$j" -X POST http://localhost:8080/api/catalog/admin/import-runs -o /dev/null -w 'seed import: %{http_code}\n'; rm -f "$j"
    ;;
  smoke)
    scripts/dev-dummy-data.sh --profile smoke
    ;;
  dummy|full)
    scripts/dev-dummy-data.sh --profile full
    ;;
  exhaustive)
    scripts/dev-dummy-data.sh --profile exhaustive
    ;;
  mirror-prod)
    scripts/dev-dummy-data.sh --profile mirror-prod
    ;;
  *)
    echo "Unknown data profile '$DATA_PROFILE' (empty|catalog|smoke|dummy|full|exhaustive|mirror-prod)" >&2
    exit 2
    ;;
esac
echo "Up. Stop with: scripts/dev-down.sh"
