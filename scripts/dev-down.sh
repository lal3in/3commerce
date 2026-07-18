#!/usr/bin/env bash
# Tear down the bare-run local env (counterpart to dev-up.sh).
# Usage: scripts/dev-down.sh [--clean|-v]
#   --clean / -v  Also remove the Postgres data volume, so the next start is a truly fresh DB.
#                 (Plain `down` keeps the volume — that is why a corrupted/mutated DB can survive
#                 restarts; use --clean, or `dev-up.sh --fresh`, when you want a clean slate.)
set -uo pipefail
cd "$(dirname "$0")/.."

DOWN_ARGS=""
case "${1:-}" in
  --clean|-v) DOWN_ARGS="-v" ;;
  "") ;;
  *) echo "Unknown argument: $1 (expected --clean|-v)" >&2; exit 2 ;;
esac

scripts/run-all.sh stop || true
pkill -f "next dev -p 3000" 2>/dev/null || true
pkill -f "3commerce.Admin" 2>/dev/null || true
pkill -f "3commerce.SupplierPortal" 2>/dev/null || true
docker compose -f docker-compose.infra.yml --profile portals down $DOWN_ARGS
echo "down${DOWN_ARGS:+ (data volume removed)}"
