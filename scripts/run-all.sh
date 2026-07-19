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

# Verbose by default so a failure carries its own diagnosis (app Debug + SQL + bus + requests).
# Dial it with: LOG_LEVEL=Information scripts/run-all.sh start  (or =Trace for everything).
log_env() {
  local lvl="${LOG_LEVEL:-Debug}"
  printf '%s\n' \
    "Logging__LogLevel__Default=$lvl" \
    "Logging__LogLevel__Microsoft.AspNetCore=Information" \
    "Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=Information" \
    "Logging__LogLevel__MassTransit=Debug"
}

start_all() {
  # Read log_env lines into an array WITHOUT `mapfile` — that builtin is bash 4+, and macOS ships
  # bash 3.2 as /bin/bash, so `#!/usr/bin/env bash` can resolve to it and break a fresh dev-up.
  LOGENV=()
  while IFS= read -r line; do LOGENV+=("$line"); done < <(log_env)
  for entry in "${APPS[@]}"; do
    name="${entry%%:*}"; rest="${entry#*:}"; path="${rest%:*}"; port="${rest##*:}"
    local -a e=( "${LOGENV[@]}" )
    # Export OTLP to the collector's published port (observability profile, always on via dev-up),
    # so bare-run traces/metrics land in Grafana instead of the console. `-` (not `:-`) so an
    # explicitly-empty OTEL_EXPORTER_OTLP_ENDPOINT= still opts a run back out to console traces.
    e+=( "OTEL_EXPORTER_OTLP_ENDPOINT=${OTEL_EXPORTER_OTLP_ENDPOINT-http://localhost:4317}" )
    [[ -n "$port" ]] && e+=( "ASPNETCORE_URLS=http://localhost:$port" )
    nohup env "${e[@]}" dotnet run --project "$path" --no-build >"$RUN_DIR/$name.log" 2>&1 &
    echo $! >"$RUN_DIR/$name.pid"; disown; echo "started $name${port:+ (:$port)} (pid $!)"
  done
}

case "${1:-start}" in
  start) start_all ;;
  stop) stop_all ;;
  *) echo "usage: $0 [start|stop]" >&2; exit 1 ;;
esac
