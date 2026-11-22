#!/usr/bin/env bash
# One-shot local-env diagnosis: infra + per-service health (manifest-driven) + recent errors from whatever
# is down. Run this FIRST when something misbehaves locally instead of hand-tailing logs.
# Maintain: services/ports come from lib/services.sh, the infra set from lib/infra.sh (auto). Add a new health or
# log surface here when infra changes.
# Exit 1 when the infra is PARTIAL (or its state is unknown): all-or-nothing means half an infra is an error.
set -uo pipefail
cd "$(dirname "$0")/.."
source scripts/lib/services.sh
source scripts/lib/infra.sh
source scripts/lib/glitchtip.sh   # the dev error inbox (ADR-0067): its issue counts
SIG='error|exception|fatal|fail|refused|unable to|cannot |timed out|denied|panic'

echo "── infra (all-or-nothing: up / down / PARTIAL — scripts/lib/infra.sh, $INFRA_SET set) ──"
infra_report; infra_rc=$?

echo "── services ──"
down=()
for entry in "${EDGE[@]}" "${SERVICES[@]}"; do
  name="${entry%%:*}"; rest="${entry#*:}"; port="${rest##*:}"
  [[ -z "$port" ]] && continue
  path=health/ready; [[ "$name" == gateway ]] && path=health
  code=$(curl -s -o /dev/null -w '%{http_code}' -m 3 "http://localhost:$port/$path" 2>/dev/null)
  mark=' '; [[ "$code" != 200 ]] && { mark='✗'; down+=("$name"); }
  # A service listens only once its bus is up (AddServiceBus waits, ≤ Messaging:StartTimeoutSeconds = 120 s, then
  # exits): 000 = still starting (waiting for RabbitMQ) or exited; 503 = listening but its DB or bus is unhealthy.
  note=''; [[ "$code" == 000 || -z "$code" ]] && note='  not listening — starting (waiting for its bus) or exited'
  printf '  %s %-12s :%-5s %s%s\n' "$mark" "$name" "$port" "${code:-DOWN}" "$note"
done

echo "── frontends ──"
for nf in storefront:3000 admin:5200 supplier-portal:5300; do
  n="${nf%%:*}"; p="${nf##*:}"
  c=$(curl -s -o /dev/null -w '%{http_code}' -m 3 "http://localhost:$p/" 2>/dev/null); [[ "$c" == 000 || -z "$c" ]] && c=down
  printf '    %-15s :%-5s %s\n' "$n" "$p" "$c"
done

echo "── observability (ADR-0065, ADR-0067) ──"
# collector health_check · Faro receiver (browser RUM; GET answers 405 when listening) · Grafana-provisioned alert rules ·
# the GlitchTip error inbox (health + its unresolved issue count per project).
for x in otel-collector:13133:/ faro-receiver:8027:/ grafana:3001:/api/health glitchtip:8000:/_health/; do
  n="${x%%:*}"; rest="${x#*:}"; p="${rest%%:*}"; pa="${rest#*:}"
  c=$(curl -s -o /dev/null -w '%{http_code}' -m 3 "http://localhost:$p$pa" 2>/dev/null); [[ "$c" == 000 || -z "$c" ]] && c=down
  printf '    %-15s :%-5s %s\n' "$n" "$p" "$c"
done
rules=$(curl -fs -m 3 -u "admin:${GRAFANA_ADMIN_PASSWORD:-admin}" http://localhost:3001/api/v1/provisioning/alert-rules 2>/dev/null)
[[ -n "$rules" ]] && printf '    %-15s %s provisioned (expected 8 — 0 means a provisioning error, see: docker logs 3commerce-grafana)\n' \
  alert-rules "$(grep -o '"uid":"tc-[a-z0-9-]*"' <<<"$rules" | wc -l | tr -d ' ')"
for gp in backend storefront; do
  issues=$(glitchtip_api "projects/3commerce/$gp/issues/?query=is:unresolved" 2>/dev/null) || continue
  printf '    %-15s %s unresolved issue(s) in %s — http://localhost:8000\n' errors "$(grep -o '"shortId"' <<<"$issues" | wc -l | tr -d ' ')" "$gp"
done

if ((${#down[@]})); then
  echo "── recent errors (services that are down) ──"
  for name in "${down[@]}"; do
    log=".run/$name.log"
    if [[ -f "$log" ]]; then
      echo "  ▼ $name  ($log)"
      grep -iE "$SIG" "$log" | tail -6 | sed 's/^/      /' || echo "      (no error lines; tail:)"; tail -3 "$log" | sed 's/^/      /'
    else
      echo "  ▼ $name  (no $log — not started?)"
    fi
  done
  echo "Tip: full log = .run/<name>.log — services AND frontends (.run/storefront.log, .run/admin.log, .run/supplier-portal.log)"
else
  echo "All services healthy."
fi
if (( infra_rc >= 2 )); then
  echo "✗ infra is $( (( infra_rc == 2 )) && echo PARTIAL || echo 'in an unknown state') — never start/stop single infra containers by hand; run scripts/dev-up.sh (heals to full) or scripts/dev-down.sh."
  exit 1
fi
