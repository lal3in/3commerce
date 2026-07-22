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

# run-all.sh stop now reaps the frontends too (by pid file, then by port). The `pkill -f` calls
# that used to live here were both a documented hazard (a pattern can match unrelated processes)
# and quietly ineffective: "next dev -p 3000" never matched the real command line, so every
# teardown left the storefront running — the source of orphans that survived for days.
scripts/run-all.sh stop || true
docker compose -f docker-compose.infra.yml --profile portals down $DOWN_ARGS
# Observability rides the app compose file — `rm -sf` (not `down`) so a containerized full stack
# (launch.sh), if one is running, is left alone. Telemetry volumes are kept, mirroring pgdata.
docker compose rm -sf otel-collector prometheus grafana loki tempo mimir >/dev/null 2>&1 || true
echo "down${DOWN_ARGS:+ (data volume removed)}"
