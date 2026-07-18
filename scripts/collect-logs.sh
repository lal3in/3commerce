#!/usr/bin/env bash
# collect-logs.sh — snapshot EVERY moving part of the local stack into one reviewable bundle.
# Purpose: a pre-production review artifact ("before we deploy, ensure all is good") — service logs,
# infra container logs, live health, the process/port inventory, migration state, and tool versions,
# all timestamped under .run/bundles/<ts>/ (+ a .tgz). Read-only: it never starts or kills anything.
#
# Usage: scripts/collect-logs.sh [--tail N] [--no-archive]
#   --tail N      Include only the last N lines of each big log (default: full logs).
#   --no-archive  Skip the .tgz (leave the directory only).
set -uo pipefail
cd "$(dirname "$0")/.."
REPO_ROOT="$(pwd)"
source scripts/lib/services.sh
source scripts/lib/procs.sh

TAIL=0; ARCHIVE=1
while [[ $# -gt 0 ]]; do
  case "$1" in
    --tail) TAIL="$2"; shift 2 ;;
    --no-archive) ARCHIVE=0; shift ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

TS="$(date +%Y%m%d-%H%M%S)"
DEST="$RUN_DIR/bundles/$TS"
mkdir -p "$DEST/services" "$DEST/frontends" "$DEST/infra"
copy_log() { # <src> <dst>
  [[ -f "$1" ]] || { echo "(no $1)" >"$2"; return; }
  if (( TAIL > 0 )); then tail -n "$TAIL" "$1" >"$2"; else cp "$1" "$2"; fi
}

echo "collecting -> $DEST"

# 1) Environment + versions (what the review needs to reproduce the state).
{
  echo "# 3commerce local-stack review bundle"
  echo "timestamp:   $TS"
  echo "repo_root:   $REPO_ROOT"
  echo "git_sha:     $(git rev-parse HEAD 2>/dev/null || echo unknown)"
  echo "git_branch:  $(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo unknown)"
  echo "git_dirty:   $([[ -n "$(git status --porcelain 2>/dev/null)" ]] && echo yes || echo no)"
  echo "os:          $(uname -a)"
  echo "dotnet:      $(dotnet --version 2>/dev/null || echo n/a)"
  echo "node:        $(node --version 2>/dev/null || echo n/a)"
  echo "npm:         $(npm --version 2>/dev/null || echo n/a)"
  echo "docker:      $(docker --version 2>/dev/null || echo n/a)"
} >"$DEST/environment.txt"

# 2) Live health (manifest-driven) + full process/port inventory.
scripts/doctor.sh >"$DEST/health.txt" 2>&1 || true
{
  repo_process_inventory
  echo
  echo "# port listeners (service + frontend ports)"
  for entry in "${EDGE[@]}" "${SERVICES[@]}" "${FRONTENDS[@]}"; do
    name="${entry%%:*}"; port="${entry##*:}"; [[ -z "$port" ]] && continue
    pid="$(lsof -tiTCP:"$port" -sTCP:LISTEN 2>/dev/null | head -1)"
    owner="free"
    if [[ -n "$pid" ]]; then is_repo_pid "$pid" && owner="repo(pid $pid)" || owner="FOREIGN(pid $pid)"; fi
    printf '  %-16s :%-5s %s\n' "$name" "$port" "$owner"
  done
} >"$DEST/processes.txt" 2>&1

# 3) The run manifest (name/port/pid/started_at/git_sha per service).
[[ -f "$RUN_DIR/stack.manifest.tsv" ]] && cp "$RUN_DIR/stack.manifest.tsv" "$DEST/stack.manifest.tsv"

# 4) Service + worker + gateway logs (current run).
for entry in "${EDGE[@]}" "${SERVICES[@]}"; do
  name="${entry%%:*}"; copy_log "$RUN_DIR/$name.log" "$DEST/services/$name.log"
done
# 5) Frontend logs.
for entry in "${FRONTENDS[@]}"; do
  name="${entry%%:*}"; copy_log "$RUN_DIR/$name.log" "$DEST/frontends/$name.log"
done

# 6) Infra container logs (our compose project only) + container state.
if docker compose -f docker-compose.infra.yml ps -q 2>/dev/null | grep -q .; then
  docker compose -f docker-compose.infra.yml ps >"$DEST/infra/containers.txt" 2>&1 || true
  docker compose -f docker-compose.infra.yml logs --no-color >"$DEST/infra/compose.log" 2>&1 || true
else
  echo "(infra compose project not running)" >"$DEST/infra/containers.txt"
  # Fall back to any archived infra logs so the bundle still carries DB/broker history.
  ls -1t "$LOG_ARCHIVE"/infra-*.log 2>/dev/null | head -1 | while IFS= read -r f; do
    [[ -n "$f" ]] && cp "$f" "$DEST/infra/compose.log"
  done
fi

# 7) EF migration state per service (has the DB caught up to the code? — a key go/no-go signal).
{
  echo "# applied vs. available migrations (last of each) per service"
  for s in $(ef_projects); do
    last_code="$(ls -1 "src/Services/$s/Infrastructure/Migrations/"*_*.cs 2>/dev/null | grep -v Designer | sed 's/.*\///' | sort | tail -1)"
    printf '  %-14s latest-in-code: %s\n' "$s" "${last_code:-none}"
  done
  echo "(To confirm DB state: dotnet ef migrations list -p src/Services/<S>/Infrastructure -s src/Services/<S>/Api)"
} >"$DEST/migrations.txt" 2>&1

# 8) Archive.
if (( ARCHIVE )); then
  ( cd "$RUN_DIR/bundles" && tar -czf "$TS.tgz" "$TS" ) && echo "archive -> .run/bundles/$TS.tgz"
fi
echo "done -> $DEST"
