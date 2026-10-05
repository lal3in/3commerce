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
  printf '  %s %-12s :%-5s %s\n' "$mark" "$name" "$port" "${code:-DOWN}"
done

echo "── frontends ──"
for nf in storefront:3000 admin:5200 supplier-portal:5300; do
  n="${nf%%:*}"; p="${nf##*:}"
  c=$(curl -s -o /dev/null -w '%{http_code}' -m 3 "http://localhost:$p/" 2>/dev/null); [[ "$c" == 000 || -z "$c" ]] && c=down
  printf '    %-15s :%-5s %s\n' "$n" "$p" "$c"
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
