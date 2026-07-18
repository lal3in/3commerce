#!/usr/bin/env bash
# Tear down the bare-run local env (counterpart to dev-up.sh) — ISOLATION-SAFE.
# Usage: scripts/dev-down.sh [--clean|-v] [--no-logs]
#   --clean / -v  Also remove the Postgres data volume, so the next start is a truly fresh DB.
#                 (Plain `down` keeps the volume — that is why a corrupted/mutated DB can survive
#                 restarts; use --clean, or `dev-up.sh --fresh`, when you want a clean slate.)
#   --no-logs     Skip archiving container logs before teardown (default: archive them).
#
# Every process stop is guarded to THIS repo (scripts/lib/procs.sh) and docker only ever touches our
# own compose project (3commerce-infra) — a co-resident project's containers/services/LLM servers are
# never signalled. NO `pkill -f` by name (banned: it can match another project's `next`/`dotnet`).
set -uo pipefail
cd "$(dirname "$0")/.."
REPO_ROOT="$(pwd)"
source scripts/lib/services.sh
source scripts/lib/procs.sh

DOWN_ARGS=""; ARCHIVE_LOGS=1
while [[ $# -gt 0 ]]; do
  case "$1" in
    --clean|-v) DOWN_ARGS="-v"; shift ;;
    --no-logs)  ARCHIVE_LOGS=0; shift ;;
    *) echo "Unknown argument: $1 (expected --clean|-v | --no-logs)" >&2; exit 2 ;;
  esac
done

# 1) Gateway + services + workers (PID-file + repo-owned-port; kills orphaned children too).
scripts/run-all.sh stop || true

# 2) Frontends — stop precisely by manifest (name+port), never by broad name match.
for entry in "${FRONTENDS[@]}"; do
  name="${entry%%:*}"; port="${entry##*:}"
  stop_tracked "$name" "$port"
done

# 3) Infra — archive container logs first (production-review habit), then down our compose project only.
if (( ARCHIVE_LOGS )) && docker compose -f docker-compose.infra.yml ps -q 2>/dev/null | grep -q .; then
  ts="$(date +%Y%m%d-%H%M%S)"
  docker compose -f docker-compose.infra.yml logs --no-color >"$LOG_ARCHIVE/infra-$ts.log" 2>&1 \
    && echo "archived infra logs -> .run/logs/infra-$ts.log"
fi
docker compose -f docker-compose.infra.yml down $DOWN_ARGS

echo "down${DOWN_ARGS:+ (data volume removed)}"
