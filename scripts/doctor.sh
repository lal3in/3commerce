#!/usr/bin/env bash
# One-shot local-env diagnosis: infra + per-service health (manifest-driven) + recent errors from whatever
# is down. Run this FIRST when something misbehaves locally instead of hand-tailing logs.
# Maintain: services/ports come from lib/services.sh (auto). Add a new health or log surface here when infra changes.
set -uo pipefail
cd "$(dirname "$0")/.."
source scripts/lib/services.sh
SIG='error|exception|fatal|fail|refused|unable to|cannot |timed out|denied|panic'

echo "── infra ──"
docker ps --format '  {{.Names}}\t{{.Status}}' 2>/dev/null | grep -E 'postgres|rabbit' \
  || echo "  (no infra running — start with: scripts/dev-up.sh)"

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
for nf in "${FRONTENDS[@]}"; do
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
  echo "Tip: full log = .run/<name>.log (history .run/logs/) · review bundle = scripts/collect-logs.sh"
else
  echo "All services healthy."
fi
