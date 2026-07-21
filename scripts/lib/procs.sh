#!/usr/bin/env bash
# Process hygiene shared by run-all.sh / dev-up.sh / dev-down.sh.
#
# Why this exists: `dotnet run` and `npm run dev` background the REAL app as a child, so a recorded
# launcher pid is not enough to guarantee the app died. Worse, starting over a live generation
# orphans it permanently — the doomed second start overwrites the .pid file, so nothing tracks the
# process that still holds the port. Ports are therefore the only honest source of truth.
# Bash 3.2 compatible (macOS ships 3.2 as /bin/bash).

# Gracefully free a TCP port: SIGTERM the listener, wait, escalate only if it refuses.
# Never `pkill -f` a pattern — that has taken out a running stack before.
reap_port() { # port [label]
  local port="$1" label="${2:-}" pids attempt
  [[ "$port" =~ ^[0-9]+$ ]] || return 0   # worker entries carry no port
  pids=$(lsof -nP -iTCP:"$port" -sTCP:LISTEN -t 2>/dev/null || true)
  [[ -z "$pids" ]] && return 0
  echo "  reaping :$port${label:+ ($label)} — pid(s) $(echo $pids | tr '\n' ' ')"
  kill -TERM $pids 2>/dev/null || true
  for attempt in $(seq 1 15); do
    sleep 1
    pids=$(lsof -nP -iTCP:"$port" -sTCP:LISTEN -t 2>/dev/null || true)
    [[ -z "$pids" ]] && return 0
  done
  echo "  :$port did not exit on SIGTERM — escalating to SIGKILL"
  kill -KILL $pids 2>/dev/null || true
}

# Drop .pid files whose process is gone, so `stop` never chases a recycled pid.
prune_stale_pids() { # run_dir
  local run_dir="${1:-.run}" f pid
  for f in "$run_dir"/*.pid; do
    [[ -e "$f" ]] || continue
    pid=$(cat "$f" 2>/dev/null)
    if [[ -z "$pid" ]] || ! ps -p "$pid" >/dev/null 2>&1; then
      rm -f "$f"
    fi
  done
}
