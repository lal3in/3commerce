# The local dev INFRA set — the ONE definition of "all of the infra", and the only code that brings it up,
# takes it down, or decides what state it is in. Source it from the repo root; it has no side effects of its own.
# Sourced by dev-up.sh, dev-down.sh, e2e-verify.sh, doctor.sh and host-check.sh (and volumes.sh, for the
# borrowed-service list). Bash 3.2 compatible (macOS /bin/bash): no associative arrays, no mapfile.
#
# The infra is ALL-OR-NOTHING. Up means every member is running AND ready; down means no member is left at all.
# Anything in between is PARTIAL — an error that doctor/host-check report, e2e-verify refuses to run on, and
# dev-up heals by bringing the rest up. Never start/stop single infra containers by hand: a hand-started
# postgres+rabbitmq looks "up" to every port check while Kafka, pgAdmin and the whole telemetry pipeline are gone.
#
# The set:
#   full (default)  every service of docker-compose.infra.yml under the `portals` profile (Postgres, RabbitMQ,
#                   Valkey, redis-exporter, Kafka, Kafka UI, pgAdmin) + the observability services bare-run dev
#                   BORROWS from docker-compose.yml (DEV_BORROWED_SERVICES: OTel collector, Prometheus, Grafana,
#                   Loki, Tempo, Mimir).
#   core            only the profile-less services of docker-compose.infra.yml (Postgres, RabbitMQ, Valkey).
#                   e2e-verify picks it on CI (CI=true): the portal JVMs + the LGTM stack starve the 2-vCPU
#                   runner and flaked unrelated browser tests. Set INFRA_SET=core|full to override.
# Members, container names and which have a Docker healthcheck are read from the compose files themselves
# (`docker compose config`), so adding a service to either file needs no edit here — only a readiness probe
# in _infra_probe if it has no healthcheck and "running" is not proof enough.
#
# Ready = running, healthy (when the service declares a healthcheck), and passing its _infra_probe.

INFRA_COMPOSE_FILE="docker-compose.infra.yml"
INFRA_PROJECT="3commerce-infra"           # `name:` of docker-compose.infra.yml
INFRA_FULL_PROFILE="portals"
APP_COMPOSE_FILE="docker-compose.yml"
APP_STACK_PROJECT="3commerce"             # `name:` of docker-compose.yml
APP_NETWORK="3commerce-data"              # docker-compose.yml's EXTERNAL default network
# The observability services bare-run dev borrows from docker-compose.yml. Also the keep-set source of
# lib/volumes.sh, so bring-up, teardown and volume pruning can never drift apart.
DEV_BORROWED_SERVICES=(otel-collector prometheus grafana loki tempo mimir)

: "${INFRA_SET:=full}"
: "${INFRA_WAIT_TIMEOUT:=300}"    # s — `docker compose up --wait` (Kafka alone may need ~120 s of healthcheck retries)
: "${INFRA_READY_TIMEOUT:=240}"   # s — then the explicit probes (Mimir holds /ready back for its ring min-ready window)
_INFRA_ROWS=""

# stdin: `docker compose config --format json`. Prints "project<TAB>service<TAB>container<TAB>hc<TAB>tier" rows.
# tier: core (no profile) | <profile> | borrowed. ONLY limits to those services and fails if one is missing.
_INFRA_PY='
import json, os, sys
d = json.load(sys.stdin)
proj = d.get("name") or os.environ["FALLBACK"]
only = os.environ.get("ONLY", "").split()
svcs = d.get("services", {})
missing = [s for s in only if s not in svcs]
if missing:
    sys.exit("services not in compose file: " + " ".join(missing))
for name in sorted(svcs):
    if only and name not in only:
        continue
    s = svcs[name]
    hc = s.get("healthcheck") or {}
    has = 1 if hc and not hc.get("disable") and hc.get("test") not in (None, [], ["NONE"]) else 0
    tier = "borrowed" if only else (",".join(s.get("profiles") or []) or "core")
    print("\t".join([proj, name, s.get("container_name") or "%s-%s-1" % (proj, name), str(has), tier]))
'

# Loads the full member table once per shell (call it before any $(…) so subshells inherit the cache).
infra_load() {
  [[ -n "$_INFRA_ROWS" ]] && return 0
  case "$INFRA_SET" in full|core) ;; *) echo "infra: INFRA_SET must be full or core (got '$INFRA_SET')" >&2; return 1 ;; esac
  local infra app
  infra="$(docker compose -f "$INFRA_COMPOSE_FILE" --profile "$INFRA_FULL_PROFILE" config --format json 2>/dev/null \
            | FALLBACK="$INFRA_PROJECT" python3 -c "$_INFRA_PY")" || infra=""
  app="$(docker compose -f "$APP_COMPOSE_FILE" --profile '*' config --format json 2>/dev/null \
            | FALLBACK="$APP_STACK_PROJECT" ONLY="${DEV_BORROWED_SERVICES[*]}" python3 -c "$_INFRA_PY")" || app=""
  if [[ -z "$infra" || -z "$app" ]]; then
    echo "infra: cannot derive the infra set — 'docker compose config' failed on $INFRA_COMPOSE_FILE or $APP_COMPOSE_FILE" >&2
    return 1
  fi
  _INFRA_ROWS="$infra"$'\n'"$app"
}

# Rows of the ACTIVE set (INFRA_SET).
_infra_rows() {
  infra_load || return 1
  if [[ "$INFRA_SET" == core ]]; then awk -F'\t' '$5 == "core"' <<<"$_INFRA_ROWS"; else printf '%s\n' "$_INFRA_ROWS"; fi
}

# docker compose over the infra file with the active set's profile.
_infra_compose() {
  if [[ "$INFRA_SET" == full ]]; then
    docker compose -f "$INFRA_COMPOSE_FILE" --profile "$INFRA_FULL_PROFILE" "$@"
  else
    docker compose -f "$INFRA_COMPOSE_FILE" "$@"
  fi
}

# Readiness beyond Docker's own view; 0 = ready. Services not listed: running (+ healthy) is enough.
_infra_probe() { # service container
  local code
  case "$1" in
    # pg_isready (the healthcheck) already answers while the entrypoint's init server — socket-only — is still
    # creating the service databases; TCP only answers once that is done, so a migrate can't race init.
    postgres)       docker exec "$2" sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -h 127.0.0.1 -U "$POSTGRES_USER" -tAc "select 1"' ;;
    redis-exporter) curl -fsS -m 3 -o /dev/null http://localhost:9121/metrics ;;
    # Any HTTP answer (even 404/405) proves the OTLP receiver is listening on the published port.
    otel-collector) code="$(curl -s -o /dev/null -w '%{http_code}' -m 3 http://localhost:4318/)"; [[ -n "$code" && "$code" != 000 ]] ;;
    prometheus)     docker exec "$2" wget -qO- http://localhost:9090/-/ready ;;   # no published port in dev
    grafana)        curl -fsS -m 3 -o /dev/null http://localhost:3001/api/health ;;
    mimir)          curl -fsS -m 3 -o /dev/null http://localhost:9009/ready ;;  # shell-less image: no healthcheck
    *)              return 0 ;;
  esac
} >/dev/null 2>&1 </dev/null

# One line per member of the active set: "service<TAB>container<TAB>status", status one of
#   ok · missing · starting · unhealthy · not-ready · exited/created/restarting/paused/dead (Docker's state) ·
#   foreign(<project>) — the container name is held, by a RUNNING container, by another compose project (e.g.
#   launch.sh's stack) or by a hand-made unlabelled one; foreign(<project>,<state>) when that container is not
#   running. A running foreign holder counts as "something is up", so the state is PARTIAL, never down.
infra_status() {
  local rows ps proj svc cname hc tier line state status_txt lproj status
  rows="$(_infra_rows)" || return 1
  ps="$(docker ps -a --format '{{.Names}}\t{{.State}}\t{{.Status}}\t{{.Label "com.docker.compose.project"}}' 2>/dev/null)" || return 1
  while IFS=$'\t' read -r proj svc cname hc tier; do
    [[ -z "$svc" ]] && continue
    line="$(awk -F'\t' -v n="$cname" '$1 == n' <<<"$ps")"
    if [[ -z "$line" ]]; then
      status=missing
    else
      IFS=$'\t' read -r _ state status_txt lproj <<<"$line"
      if [[ "$lproj" != "$proj" ]]; then status="foreign(${lproj:-unlabelled}"; [[ "$state" == running ]] || status="$status,$state"; status="$status)"
      elif [[ "$state" != running ]]; then status="$state"
      elif [[ "$hc" == 1 && "$status_txt" != *"(healthy)"* ]]; then
        case "$status_txt" in *unhealthy*) status=unhealthy ;; *) status=starting ;; esac
      elif _infra_probe "$svc" "$cname"; then status=ok
      else status=not-ready
      fi
    fi
    printf '%s\t%s\t%s\n' "$svc" "$cname" "$status"
  done <<<"$rows"
}

# stdin: infra_status lines. Prints up | down | PARTIAL (missing: svc(status) …); returns 0 | 1 | 2.
_infra_summarise() {
  local svc c s total=0 ok=0 live=0 bad=""
  while IFS=$'\t' read -r svc c s; do
    [[ -z "$svc" ]] && continue
    total=$((total + 1))
    if [[ "$s" == ok ]]; then ok=$((ok + 1)); live=1; elif [[ "$s" == missing ]]; then bad="$bad $svc"; else bad="$bad $svc($s)"; fi
    case "$s" in foreign*,*) ;; starting|unhealthy|not-ready|restarting|paused|foreign*) live=1 ;; esac
  done
  if (( total > 0 && ok == total )); then echo up; return 0; fi
  if (( live == 0 )); then echo down; return 1; fi
  echo "PARTIAL (missing:$bad)"; return 2
}

# Prints up | down | PARTIAL (missing: …) | unknown (…); returns 0 up · 1 down · 2 PARTIAL · 3 unknown.
infra_state() {
  local st
  infra_load 2>/dev/null || { echo "unknown (infra set unreadable — docker compose config failed)"; return 3; }
  st="$(infra_status)" || { echo "unknown (Docker not responding)"; return 3; }
  _infra_summarise <<<"$st"
}

# Human report (doctor.sh / host-check.sh): a line per member unless fully down, then the state. Returns infra_state's code.
infra_report() {
  local st svc c s mark state rc
  infra_load 2>/dev/null || { echo "  ✗ state: unknown — infra set unreadable (docker compose config failed)"; return 3; }
  st="$(infra_status)" || { echo "  ✗ state: unknown — Docker not responding (start it: colima start)"; return 3; }
  state="$(_infra_summarise <<<"$st")"; rc=$?
  if (( rc != 1 )); then
    while IFS=$'\t' read -r svc c s; do
      mark='✓'; [[ "$s" == ok ]] || mark='✗'
      printf '  %s %-15s %-26s %s\n' "$mark" "$svc" "$c" "$s"
    done <<<"$st"
  fi
  case $rc in
    0) echo "  state: up ($(grep -c . <<<"$st") containers, $INFRA_SET set)" ;;
    1) echo "  state: down — start it with: scripts/dev-up.sh" ;;
    *) echo "  ✗ state: $state — heal with scripts/dev-up.sh, or clear with scripts/dev-down.sh" ;;
  esac
  return $rc
}

# Brings the WHOLE active set up and proves it: heals a partial state (only missing/stopped members start;
# running ones are left alone, unhealthy ones are restarted), waits on every healthcheck and probe, and
# returns non-zero naming each member that is not ready. Idempotent: on a fully-up set it changes nothing.
infra_up() {
  local start=$SECONDS st foreign svc c s rc=0 limit t0 state total
  infra_load || return 1
  st="$(infra_status)" || { echo "infra: Docker is not responding (start it: colima start)" >&2; return 1; }
  total="$(grep -c . <<<"$st")"
  # compose dies on a container-name conflict halfway through `up`, which is itself a partial state — refuse first.
  foreign="$(awk -F'\t' '$3 ~ /^foreign/ {printf "    %s  %s\n", $2, $3}' <<<"$st")"
  if [[ -n "$foreign" ]]; then
    echo "infra: refusing to start — these container names are held by something that is not the dev infra:" >&2
    echo "$foreign" >&2
    echo "  A containerised stack (scripts/launch.sh)? Stop it first (docker compose -p <project> down)." >&2
    echo "  Unlabelled (hand-made with docker run)? scripts/dev-down.sh removes those." >&2
    return 1
  fi
  if [[ "$(_infra_summarise <<<"$st")" == up ]]; then
    echo "  infra: already up — all $total containers ready ($INFRA_SET set), nothing to do"
    return 0
  fi
  # `compose up` leaves a RUNNING container alone, so an unhealthy or paused member would otherwise sit there forever.
  while IFS=$'\t' read -r svc c s; do
    case "$s" in
      unhealthy) echo "  infra: restarting unhealthy $c"; docker restart "$c" >/dev/null || rc=1 ;;
      paused)    echo "  infra: unpausing $c"; docker unpause "$c" >/dev/null || rc=1 ;;
    esac
  done <<<"$st"
  echo "  infra: bringing up the $INFRA_SET set ($total containers) and waiting for health …"
  _infra_compose up -d --wait --wait-timeout "$INFRA_WAIT_TIMEOUT" || rc=1
  if [[ "$INFRA_SET" == full ]]; then
    # docker-compose.yml's default network is EXTERNAL (owned by docker-compose.db.yml, which bare-run never runs),
    # so create it or compose refuses to start. Named services + --no-deps only: a bare `--profile observability up`
    # would also build/start the 13 app containers (they carry no profile).
    docker network inspect "$APP_NETWORK" >/dev/null 2>&1 || docker network create "$APP_NETWORK" >/dev/null || rc=1
    docker compose -f "$APP_COMPOSE_FILE" up -d --no-deps --wait --wait-timeout "$INFRA_WAIT_TIMEOUT" \
      "${DEV_BORROWED_SERVICES[@]}" || rc=1
  fi
  # compose --wait covers running + healthchecks; the explicit probes cover the rest. Don't wait out the full
  # window when compose already reported a failure — a crashed/unhealthy container won't fix itself.
  limit=$INFRA_READY_TIMEOUT; (( rc != 0 )) && limit=20
  t0=$SECONDS
  until state="$(infra_state)"; do
    if (( SECONDS - t0 >= limit )); then
      echo "infra: NOT fully up after $((SECONDS - start))s — $state" >&2
      infra_report >&2
      return 1
    fi
    sleep 3
  done
  echo "  infra: up — all $total containers running + ready ($INFRA_SET set, $((SECONDS - start))s)"
}

# Every container of the dev infra that still exists, in any state: anything in the infra compose project (all
# profiles, overlays' services), the borrowed services in the app project, and unlabelled containers squatting a
# member's name. Containers of a containerised stack (launch.sh) other than the borrowed ones are not dev infra.
infra_survivors() {
  local ps names
  infra_load || return 1
  names=" $(awk -F'\t' '{printf "%s ", $3}' <<<"$_INFRA_ROWS")"
  ps="$(docker ps -a --format '{{.Names}}\t{{.State}}\t{{.Label "com.docker.compose.project"}}\t{{.Label "com.docker.compose.service"}}' 2>/dev/null)" || return 1
  awk -F'\t' -v ip="$INFRA_PROJECT" -v ap="$APP_STACK_PROJECT" -v bs=" ${DEV_BORROWED_SERVICES[*]} " -v names="$names" '
    $3 == ip || ($3 == ap && index(bs, " " $4 " ")) || ($3 == "" && index(names, " " $1 " ")) { printf "    %s (%s)\n", $1, $2 }' <<<"$ps"
}

# Stops and removes EVERY member — always the full set whatever INFRA_SET says, every profile of the infra file,
# orphans its overlays left (pgbouncer, kafka), the borrowed services, and hand-made squatters — then proves
# none remain, naming any survivor. Named volumes are kept unless -v (they hold the dev DB; never `volume prune`).
# Idempotent: a second down is a no-op that still verifies.
infra_down() { # [-v]
  local vol="${1:-}" rc=0 c survivors
  docker info >/dev/null 2>&1 || { echo "infra: Docker is not responding — cannot take the infra down or prove it is down (start it: colima start)" >&2; return 1; }
  infra_load || rc=1
  docker compose -f "$INFRA_COMPOSE_FILE" --profile '*' down --remove-orphans $vol || rc=1
  # `rm -s` (not `down`) so a containerised stack's own services are left alone; -v drops only the borrowed
  # containers' ANONYMOUS volumes (named telemetry volumes are kept, mirroring pgdata).
  docker compose -f "$APP_COMPOSE_FILE" rm -sfv "${DEV_BORROWED_SERVICES[@]}" || rc=1
  # Hand-made squatters: a member's container name on a container with no compose project label (docker run).
  for c in $(docker ps -a --format '{{.Names}}\t{{.Label "com.docker.compose.project"}}' 2>/dev/null \
               | awk -F'\t' -v names=" $(awk -F'\t' '{printf "%s ", $3}' <<<"$_INFRA_ROWS")" '$2 == "" && index(names, " " $1 " ") {print $1}'); do
    echo "  infra: removing hand-made container $c (no compose project label)"
    docker rm -f "$c" >/dev/null || rc=1
  done
  survivors="$(infra_survivors)" || { echo "infra: could not list containers to verify the down" >&2; return 1; }
  if [[ -n "$survivors" ]]; then
    echo "infra: NOT fully down — still present:" >&2
    echo "$survivors" >&2
    return 1
  fi
  return $rc
}
