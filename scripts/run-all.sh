#!/usr/bin/env bash
# Starts the gateway, all DB-owning services, and workers via bare `dotnet run` (ADR-0009).
# Service list comes from scripts/lib/services.sh (single source of truth).
# Usage: scripts/run-all.sh [start|stop]   Logs: .run/<name>.log   PIDs: .run/<name>.pid
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/lib/services.sh

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
RUN_DIR=.run; mkdir -p "$RUN_DIR"
APPS=( "${EDGE[@]}" "${SERVICES[@]}" )

stop_all() {
  for entry in "${APPS[@]}"; do
    name="${entry%%:*}"
    [[ -f "$RUN_DIR/$name.pid" ]] && { kill "$(cat "$RUN_DIR/$name.pid")" 2>/dev/null || true; rm -f "$RUN_DIR/$name.pid"; }
  done
  echo "stopped"
}

start_all() {
  for entry in "${APPS[@]}"; do
    name="${entry%%:*}"; rest="${entry#*:}"; path="${rest%:*}"; port="${rest##*:}"
    if [[ -n "$port" ]]; then
      ASPNETCORE_URLS="http://localhost:$port" dotnet run --project "$path" --no-build >"$RUN_DIR/$name.log" 2>&1 &
    else
      dotnet run --project "$path" --no-build >"$RUN_DIR/$name.log" 2>&1 &
    fi
    echo $! >"$RUN_DIR/$name.pid"; echo "started $name${port:+ (:$port)} (pid $!)"
  done
}

case "${1:-start}" in
  start) start_all ;;
  stop) stop_all ;;
  *) echo "usage: $0 [start|stop]" >&2; exit 1 ;;
esac
