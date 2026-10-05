#!/usr/bin/env bash
# CI runner resource diagnostics for the browser-e2e job (Linux runners only).
#
#   scripts/ci-resources.sh start    # print the runner's size, then sample in the background every 30 s
#   scripts/ci-resources.sh report   # peak load, lowest available memory, the containers at that moment,
#                                    # and the Playwright tally + which portal specs ran or skipped
#
# Why: e2e-verify boots AND tears down the stack inside one step, so a later step can't look at it.
# The sampler sees the stack while it is up. Starvation shows up as load far above nproc or available
# memory near zero, often alongside "flaky" Playwright tests (CI retries them, so they still go green).
set -uo pipefail

DIR="${RUNNER_TEMP:-/tmp}/ci-resources"
SAMPLES="$DIR/samples.log"
PW_LOG=/tmp/3c-playwright.log   # where e2e-verify.sh L20 writes the Playwright output

runner_size() {
  echo "nproc=$(nproc)  mem_total=$(free -m | awk '/^Mem:/ {print $2}')M  swap=$(free -m | awk '/^Swap:/ {print $2}')M"
  df -h / | tail -1 | awk '{print "disk /: size=" $2 " used=" $3 " avail=" $4}'
}

case "${1:-}" in
  start)
    mkdir -p "$DIR"
    runner_size
    nohup bash -c '
      while :; do
        read -r l1 l5 l15 _ < /proc/loadavg
        mem=$(free -m | awk "/^Mem:/ {print \"used=\" \$3 \"M avail=\" \$7 \"M\"}")
        echo "== $(date -u +%T) load=$l1 load5=$l5 $mem containers=$(docker ps -q | wc -l)"
        docker stats --no-stream --format "   {{.Name}} cpu={{.CPUPerc}} mem={{.MemUsage}}" 2>/dev/null
        sleep 30
      done' >>"$SAMPLES" 2>&1 &
    echo "sampler pid $! -> $SAMPLES"
    ;;
  report)
    runner_size
    if [[ -s "$SAMPLES" ]]; then
      n=$(grep -c '^== ' "$SAMPLES")
      peak=$(grep -o 'load=[0-9.]*' "$SAMPLES" | cut -d= -f2 | sort -n | tail -1)
      minav=$(grep -o 'avail=[0-9]*' "$SAMPLES" | cut -d= -f2 | sort -n | head -1)
      maxc=$(grep -o 'containers=[0-9]*' "$SAMPLES" | cut -d= -f2 | sort -n | tail -1)
      echo "samples=$n (every 30 s)  peak load1=$peak  lowest mem avail=${minav}M  max containers=$maxc"
      echo "--- timeline ---"
      grep '^== ' "$SAMPLES"
      echo "--- containers at the lowest-available-memory sample (avail=${minav}M) ---"
      awk -v m="avail=${minav}M" '/^== / {p = index($0, m) > 0 && !done; if (p) {print; done = 1}; next} p' "$SAMPLES"
    else
      echo "no resource samples (sampler not started?)"
    fi
    if [[ -s "$PW_LOG" ]]; then
      echo "--- Playwright tally ---"
      grep -E '^  [0-9]+ (passed|failed|flaky|skipped|interrupted|did not run)' "$PW_LOG"
      awk '/^  [0-9]+ (flaky|failed)/ {f=1; print; next} f && /^    / {print; next} {f=0}' "$PW_LOG"
      echo "--- portal specs (✓ ran+passed, - skipped, ✘ failed) ---"
      grep -E 'infra-portals|observability-portals' "$PW_LOG" | grep -E '^ +[^ ]+ +[0-9]+ \[' | cut -c1-200
    fi
    ;;
  *) echo "usage: $0 start|report" >&2; exit 2 ;;
esac
