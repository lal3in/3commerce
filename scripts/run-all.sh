#!/usr/bin/env bash
# Starts the gateway, all six services, and the Notifications worker (ADR-0009: bare dotnet run).
# Usage: scripts/run-all.sh [start|stop]
# Logs:  .run/<name>.log   PIDs: .run/<name>.pid
set -euo pipefail
cd "$(dirname "$0")/.."

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

RUN_DIR=.run
mkdir -p "$RUN_DIR"

declare -a APPS=(
  "gateway:src/Gateway"
  "identity:src/Services/Identity/Api"
  "catalog:src/Services/Catalog/Api"
  "ordering:src/Services/Ordering/Api"
  "payments:src/Services/Payments/Api"
  "fulfillment:src/Services/Fulfillment/Api"
  "support:src/Services/Support/Api"
  "notifications:src/Workers/Notifications"
)

stop_all() {
  for entry in "${APPS[@]}"; do
    name="${entry%%:*}"
    if [[ -f "$RUN_DIR/$name.pid" ]]; then
      kill "$(cat "$RUN_DIR/$name.pid")" 2>/dev/null || true
      rm -f "$RUN_DIR/$name.pid"
    fi
  done
  echo "stopped"
}

start_all() {
  for entry in "${APPS[@]}"; do
    name="${entry%%:*}" path="${entry#*:}"
    dotnet run --project "$path" --no-build >"$RUN_DIR/$name.log" 2>&1 &
    echo $! >"$RUN_DIR/$name.pid"
    echo "started $name (pid $!)"
  done
}

case "${1:-start}" in
  start) start_all ;;
  stop)  stop_all ;;
  *) echo "usage: $0 [start|stop]" >&2; exit 1 ;;
esac
