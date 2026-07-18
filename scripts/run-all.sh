#!/usr/bin/env bash
# Starts the gateway, all DB-owning services, and workers via bare `dotnet run` (ADR-0009).
# Service list comes from scripts/lib/services.sh (single source of truth).
# Usage: scripts/run-all.sh [start|stop|restart|status]
#   Logs (current run): .run/<name>.log     Rotated history: .run/logs/<name>.<ts>.log
#   PIDs: .run/<name>.pid                    Run manifest: .run/stack.manifest.tsv
#
# start is IDEMPOTENT and orphan-proof: it stops any prior repo instance (including orphaned
# child servers left by a previous `dotnet run`) before relaunching, and refuses to clobber a
# port owned by a NON-repo process (a co-resident project). See scripts/lib/procs.sh.
set -uo pipefail
cd "$(dirname "$0")/.."
REPO_ROOT="$(pwd)"
source scripts/lib/services.sh
source scripts/lib/procs.sh

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
APPS=( "${EDGE[@]}" "${SERVICES[@]}" )
MANIFEST="$RUN_DIR/stack.manifest.tsv"

stop_all() {
  for entry in "${APPS[@]}"; do
    name="${entry%%:*}"; rest="${entry#*:}"; port="${rest##*:}"
    stop_tracked "$name" "$port"
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

  local sha; sha="$(git rev-parse --short HEAD 2>/dev/null || echo unknown)"
  { printf 'name\tport\tpid\tstarted_at\tgit_sha\tlog\n'; } >"$MANIFEST"

  for entry in "${APPS[@]}"; do
    name="${entry%%:*}"; rest="${entry#*:}"; path="${rest%:*}"; port="${rest##*:}"
    local -a e=( "${LOGENV[@]}" )
    [[ -n "$port" ]] && e+=( "ASPNETCORE_URLS=http://localhost:$port" )
    # Absolute --project path so the `dotnet run` WRAPPER's command line contains the repo root — that
    # is what makes is_repo_pid recognise it as ours (the relative form would not). See procs.sh.
    if start_tracked "$name" "$port" -- env "${e[@]}" dotnet run --project "$REPO_ROOT/$path" --no-build; then
      printf '%s\t%s\t%s\t%s\t%s\t%s\n' \
        "$name" "${port:-—}" "$(cat "$RUN_DIR/$name.pid" 2>/dev/null)" "$(date +%FT%T)" "$sha" ".run/$name.log" >>"$MANIFEST"
    fi
  done
  echo "manifest -> $MANIFEST"
}

status_all() {
  printf '  %-14s %-6s %-8s %s\n' NAME PORT PID STATE
  for entry in "${APPS[@]}"; do
    name="${entry%%:*}"; rest="${entry#*:}"; port="${rest##*:}"
    pid="$(cat "$RUN_DIR/$name.pid" 2>/dev/null || true)"
    state="down"
    if is_repo_pid "$pid"; then state="up"; elif [[ -n "$port" ]] && repo_listener "$port" >/dev/null 2>&1; then state="up(orphan-pidfile)"; fi
    printf '  %-14s %-6s %-8s %s\n' "$name" "${port:-—}" "${pid:-—}" "$state"
  done
}

case "${1:-start}" in
  start)   start_all ;;
  stop)    stop_all ;;
  restart) stop_all; start_all ;;
  status)  status_all ;;
  *) echo "usage: $0 [start|stop|restart|status]" >&2; exit 1 ;;
esac
