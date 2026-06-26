#!/usr/bin/env bash
# host-check.sh — full-stack diagnosis across one or more hosts (localhost, an SSH VPS, or a cloud VM).
# The SAME sweep runs over a pluggable transport, so it scales from your Mac to Hostinger/EC2/GCE/Azure.
#
# Usage:
#   scripts/host-check.sh [--deep] [--logs] [target ...]
#     target : a name from scripts/lib/hosts.sh, or 'all'. Default: local.
#     --deep : also outbox backlog, migration drift, OOM-kill scan (slower).
#     --logs : also pull provider-managed logs (CloudWatch/GCP/Azure) when configured in hosts.sh.
set -uo pipefail
cd "$(dirname "$0")/.."
source scripts/lib/services.sh
source scripts/lib/hosts.sh

DEEP=0; LOGS=0; want=()
for a in "$@"; do case "$a" in
  --deep) DEEP=1;; --logs) LOGS=1;; --*) ;; *) want+=("$a");;
esac; done
[[ ${#want[@]} -eq 0 ]] && want=(local)
[[ "${want[0]}" == all ]] && want=($(printf '%s\n' "${HOST_TARGETS[@]}" | cut -d'|' -f1))

PORTS="$(for e in "${EDGE[@]}" "${SERVICES[@]}"; do p="${e##*:}"; [[ -n "$p" ]] && printf '%s ' "$p"; done)"

# ── transport: run a command ON a target (local | ssh | gcp) ─────────────────────────────────────
run_on() { # run_on "<transport>|<detail>" "<remote shell>"
  local tr="${1%%|*}" d="${1#*|}" cmd="$2"
  case "$tr" in
    local) bash -lc "$cmd" 2>&1 ;;
    ssh)   local hp="$d" port=22; [[ "$d" == *:* ]] && { hp="${d%:*}"; port="${d##*:}"; }
           ssh -o ConnectTimeout=8 -o BatchMode=yes -p "$port" "$hp" "$cmd" 2>&1 ;;
    gcp)   gcloud compute ssh "${d#*/}" --zone "${d%%/*}" --command "$cmd" -q 2>&1 ;;
    *)     echo "(transport '$tr' not implemented — use ssh to the instance IP)";;
  esac
}

# ── the probe: one self-contained snippet that runs on the target and reports everything ─────────
probe() {
cat <<PROBE
set +e
osr="\$(uname -s)"
echo "── containers ──"
docker ps --format '  {{.Names}}\t{{.Status}}' 2>/dev/null || echo "  (docker unavailable)"

echo "── service health (bare-run host ports; containerized shows above as (healthy)) ──"
for p in $PORTS; do
  c=\$(curl -s -o /dev/null -w '%{http_code}' -m3 "http://localhost:\$p/health/ready" 2>/dev/null)
  [ "\$p" = 8080 ] && c=\$(curl -s -o /dev/null -w '%{http_code}' -m3 "http://localhost:8080/health" 2>/dev/null)
  [ "\$c" != 200 ] && printf '  :%s %s\n' "\$p" "\${c:-down}"
done | grep . || echo "  all reporting 200 (or not bare-run on this host)"

echo "── bus (RabbitMQ queues: messages / consumers / unacked) ──"
rb=\$(docker ps --format '{{.Names}}' 2>/dev/null | grep -m1 -i rabbit)
if [ -n "\$rb" ]; then
  docker exec "\$rb" rabbitmqctl -q list_queues name messages consumers messages_unacknowledged 2>/dev/null \
    | awk '\$2 ~ /^[0-9]+\$/ && (\$2>0 || \$3==0){f=1; print "  "\$0 ((\$3==0 && \$2>0)?"   <-- STUCK (no consumer)":"")} END{if(!f)print "  (all queues drained, consumers attached)"}'
else echo "  (no rabbitmq container)"; fi

echo "── infra logs (recent errors) ──"
for c in \$(docker ps --format '{{.Names}}' 2>/dev/null | grep -E 'postgres|rabbit'); do
  e=\$(docker logs --tail 40 "\$c" 2>&1 | grep -iE 'error|fatal|panic|refused' | tail -3)
  [ -n "\$e" ] && { echo "  [\$c]"; echo "\$e" | sed 's/^/    /'; }
done; true

echo "── observability ──"
for x in 9090:prometheus:/-/healthy 3001:grafana:/api/health 13133:otel:/; do
  pt=\${x%%:*}; nm=\$(echo "\$x"|cut -d: -f2); pa=\${x##*:}
  cc=\$(curl -s -o /dev/null -w '%{http_code}' -m2 http://localhost:\$pt\$pa 2>/dev/null); { [ "\$cc" = 000 ] || [ -z "\$cc" ]; } && cc=down
  printf '  %-11s %s\n' "\$nm" "\$cc"
done

echo "── compose ──"
docker compose ls 2>/dev/null | tail -n +2 | sed 's/^/  /' || echo "  (not a compose host)"

echo "── host resources ──"
if [ "\$osr" = Linux ]; then free -h 2>/dev/null | awk 'NR<=2{print "  "\$0}'; else vm_stat 2>/dev/null | head -4 | sed 's/^/  /'; fi
df -h / 2>/dev/null | awk 'NR==2{print "  disk: "\$4" free of "\$2}'
docker info --format '  docker: {{.NCPU}} cpu / {{.MemTotal}} bytes' 2>/dev/null

if [ "$DEEP" = 1 ]; then
  echo "── deep: daemon errors ──"
  if [ "\$osr" = Linux ]; then journalctl -p err --since '15 min ago' --no-pager 2>/dev/null | tail -5 | sed 's/^/  /'
  else log show --last 15m --predicate 'eventMessage CONTAINS "memorystatus" OR eventMessage CONTAINS "lowmem"' 2>/dev/null | tail -5 | sed 's/^/  /'; fi
fi
PROBE
}

sweep() { # sweep "<name>" "<transport|detail>"
  local name="$1" spec="$2"
  echo; echo "════════════════ $name  ($spec) ════════════════"
  if ! run_on "$spec" 'echo ok' >/dev/null 2>&1; then echo "  ✗ UNREACHABLE"; return; fi
  run_on "$spec" "$(probe)"

  # local-only host extras (the Docker VM that OOMs)
  if [[ "${spec%%|*}" == local ]]; then
    echo "── colima/docker VM (the OOM source) ──"
    for f in ~/.colima/_lima/colima/ha.stderr.log ~/.colima/_lima/colima/serialv.log; do
      [[ -f "$f" ]] && { e=$(grep -iE 'oom|out of memory|cannot allocate|killed|qemu.*(exit|abort)|panic|fatal' "$f" 2>/dev/null | grep -v 'forwarding tcp port' | tail -3); [[ -n "$e" ]] && { echo "  [$f]"; echo "$e" | cut -c1-160 | sed 's/^/    /'; }; }
    done; true
  fi

  if (( DEEP )); then
    echo "── deep: outbox backlog + migration drift (per service DB) ──"
    run_on "$spec" "$(deep_db_probe)"
  fi
}

deep_db_probe() {
cat <<'DB'
pg=$(docker ps --format '{{.Names}}' 2>/dev/null | grep -m1 -i postgres)
[ -z "$pg" ] && { echo "  (no postgres)"; exit 0; }
for db in $(docker exec "$pg" psql -U postgres -tAc "select datname from pg_database where datname like '%\_db'" 2>/dev/null); do
  n=$(docker exec "$pg" psql -U postgres -d "$db" -tAc "select count(*) from \"OutboxMessage\" where \"ProcessedAt\" is null" 2>/dev/null)
  [ -n "$n" ] && [ "$n" != 0 ] && echo "  $db: $n unprocessed outbox messages  <-- bus backlog"
done; true
DB
}

# ── provider-managed logs (optional; needs the CLI + a configured target) ────────────────────────
provider_logs() {
  echo; echo "════════════════ provider-managed logs ════════════════"
  if command -v aws >/dev/null && [[ -n "$CW_LOG_GROUP" ]]; then
    echo "── AWS CloudWatch ($CW_LOG_GROUP) ──"; aws logs tail "$CW_LOG_GROUP" --since 15m --format short 2>&1 | grep -iE 'error|exception|fail' | tail -10 | sed 's/^/  /'
  fi
  if command -v gcloud >/dev/null && [[ -n "$GCP_PROJECT" ]]; then
    echo "── GCP Cloud Logging ($GCP_PROJECT) ──"; gcloud logging read 'severity>=ERROR' --project "$GCP_PROJECT" --freshness=15m --limit=10 --format='value(timestamp,textPayload)' 2>&1 | sed 's/^/  /'
  fi
  if command -v az >/dev/null && [[ -n "$AZ_WORKSPACE" ]]; then
    echo "── Azure Log Analytics ($AZ_WORKSPACE) ──"; az monitor log-analytics query -w "$AZ_WORKSPACE" --analytics-query "union AppExceptions, ContainerLog | where TimeGenerated > ago(15m) | take 10" -o table 2>&1 | sed 's/^/  /'
  fi
  [[ -z "$CW_LOG_GROUP$GCP_PROJECT$AZ_WORKSPACE" ]] && echo "  (none configured — set CW_LOG_GROUP / GCP_PROJECT / AZ_WORKSPACE in scripts/lib/hosts.sh)"
}

for name in "${want[@]}"; do
  spec="$(for t in "${HOST_TARGETS[@]}"; do [[ "${t%%|*}" == "$name" ]] && echo "${t#*|}"; done)"
  [[ -z "$spec" && "$name" != local ]] && { echo "unknown target '$name' (see scripts/lib/hosts.sh)"; continue; }
  [[ "$name" == local ]] && spec="local|"
  sweep "$name" "$spec"
done
(( LOGS )) && provider_logs
echo
