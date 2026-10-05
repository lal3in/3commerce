#!/usr/bin/env bash
# Docker volumes the bare-run dev stack (dev-up.sh / dev-down.sh) and the containerised stack (launch.sh)
# share or leave behind. Source it; it defines no side effects of its own.
#
# The two modes use different compose projects for the same kinds of things:
#   3commerce-infra  docker-compose.infra.yml  → the dev stack's own volumes (pgdata, rabbitmq, valkey, …)
#   3commerce        docker-compose.yml        → the containerised app stack. Bare-run dev BORROWS only its
#                                                observability services (below) — their volumes are in use in
#                                                both modes. Everything else in this project (rabbitmq_data, …)
#                                                is the containerised stack's own state, which nothing mounts
#                                                while you work bare-run: an orphan, shown "unused" in Docker.
#   3commerce-db     docker-compose.db.yml     → launch.sh's external Postgres, deliberately persisted across
#                                                launches. NEVER pruned from here.

# DEV_BORROWED_SERVICES (the observability services dev-up.sh borrows from docker-compose.yml) and
# APP_STACK_PROJECT live in lib/infra.sh — the one definition of the infra set. Bring-up, teardown and the
# "keep" set below all read that list, so the three can never drift apart.
source "$(dirname "${BASH_SOURCE[0]}")/infra.sh"

# Volume keys (e.g. "loki_data") mounted by the borrowed services, derived from the compose file itself.
# Prints nothing if the compose config cannot be read — callers treat that as "keep everything".
_borrowed_volume_keys() {
  docker compose -f docker-compose.yml --profile '*' config --format json 2>/dev/null \
    | SVC="${DEV_BORROWED_SERVICES[*]}" python3 -c '
import json, os, sys
try:
    d = json.load(sys.stdin)
except Exception:
    sys.exit(0)
keys = {v["source"] for s in os.environ["SVC"].split()
        for v in d.get("services", {}).get(s, {}).get("volumes", []) if v.get("type") == "volume"}
print("\n".join(sorted(keys)))'
}

# Prints the names of containerised-app volumes that bare-run dev never borrows AND that no container — running
# or stopped — mounts. Fails SAFE: if the borrowed set cannot be derived it prints nothing, because an empty
# keep-set would otherwise make the in-use telemetry volumes look like orphans.
app_stack_orphan_volumes() {
  local keep v key
  keep="$(_borrowed_volume_keys || true)"   # never let a failed read abort a `set -e` caller
  if [[ -z "$keep" ]]; then
    echo "  (volumes: could not read docker-compose.yml's observability volumes — pruning nothing)" >&2
    return 0
  fi
  for v in $(docker volume ls -q --filter "label=com.docker.compose.project=$APP_STACK_PROJECT" 2>/dev/null); do
    key="$(docker volume inspect "$v" --format '{{index .Labels "com.docker.compose.volume"}}' 2>/dev/null)"
    grep -qxF "$key" <<<"$keep" && continue                          # borrowed by the dev stack
    [[ -n "$(docker ps -aq --filter "volume=$v" 2>/dev/null)" ]] && continue  # something still mounts it
    echo "$v"
  done
}

# Removes the orphans (used by dev-down.sh --clean, and so dev-up.sh --fresh: a run that is throwing state
# away anyway). Each removal is re-checked immediately before it happens.
prune_app_stack_orphans() {
  local v
  for v in $(app_stack_orphan_volumes); do
    if [[ -z "$(docker ps -aq --filter "volume=$v" 2>/dev/null)" ]] && docker volume rm "$v" >/dev/null 2>&1; then
      echo "  removed orphaned volume $v (containerised-stack state; bare-run dev never uses it)"
    fi
  done
}

# Names the orphans without touching them (plain dev-up.sh / dev-down.sh: a containerised stack you intend to
# resume with `launch.sh --reuse` would need them).
report_app_stack_orphans() {
  local orphans
  orphans="$(app_stack_orphan_volumes | tr '\n' ' ')"
  [[ -z "${orphans// /}" ]] && return 0
  echo "  note: unused containerised-stack volume(s): ${orphans% } — removed automatically by"
  echo "        'dev-down.sh --clean' / 'dev-up.sh --fresh' (or now: docker volume rm ${orphans% })"
}
