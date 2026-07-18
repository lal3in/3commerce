# scripts/lib/procs.sh — isolation-safe process + log management for the bare-run dev stack.
#
# WHY THIS EXISTS: this machine routinely runs OTHER projects' containers, services and local LLM
# servers next to ours. Every process signal here is GUARDED — we never kill a PID whose command line
# does not reference THIS repo, and we only ever touch our own docker-compose project. A broad
# `pkill -f next` or `docker stop $(docker ps -q)` would take down a co-resident project; those are
# banned (see AGENTS.md / memory: no pkill patterns matching stack processes).
#
# It also fixes the "16-hour zombie server" bug: `dotnet run` / `next dev` are WRAPPERS that spawn the
# real HTTP server as a CHILD. Killing only the recorded wrapper PID orphaned the child, which kept
# serving stale code across sessions. stop_tracked kills the wrapper AND, as a safety net, the
# repo-owned LISTENER on the service's known port — so an orphan can never survive a stop.
#
# bash 3.2 compatible (macOS /bin/bash): no mapfile, no associative arrays.

# REPO_ROOT: every real entrypoint (run-all/dev-up/dev-down/collect-logs) sets it before sourcing.
# Fallbacks below only cover an ad-hoc `source`: use BASH_SOURCE under bash, else assume the caller's
# CWD is the repo root (true for all our scripts). Avoids zsh's empty BASH_SOURCE resolving upward.
if [[ -z "${REPO_ROOT:-}" ]]; then
  if [[ -n "${BASH_SOURCE:-}" ]]; then
    REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
  else
    REPO_ROOT="$(pwd)"
  fi
fi
RUN_DIR="$REPO_ROOT/.run"
LOG_ARCHIVE="$RUN_DIR/logs"
mkdir -p "$LOG_ARCHIVE"

# Keep at most this many rotated logs per service (older ones are pruned on each start).
: "${LOG_KEEP:=10}"

# is_repo_pid <pid> — true only if the process is alive AND its command references this repo.
# The gate that makes every kill isolation-safe.
is_repo_pid() {
  local pid="$1" cmd
  [[ -n "$pid" ]] || return 1
  kill -0 "$pid" 2>/dev/null || return 1
  cmd="$(ps -o command= -p "$pid" 2>/dev/null)" || return 1
  [[ "$cmd" == *"$REPO_ROOT"* ]]
}

# repo_listener <port> — echo the LISTEN pid on <port> IFF it is repo-owned; else nothing (rc 1).
repo_listener() {
  local port="$1" pid
  for pid in $(lsof -tiTCP:"$port" -sTCP:LISTEN 2>/dev/null); do
    if is_repo_pid "$pid"; then echo "$pid"; return 0; fi
  done
  return 1
}

# foreign_listener <port> — echo a NON-repo pid holding <port> (so callers WARN, never kill it).
foreign_listener() {
  local port="$1" pid
  for pid in $(lsof -tiTCP:"$port" -sTCP:LISTEN 2>/dev/null); do
    is_repo_pid "$pid" || { echo "$pid"; return 0; }
  done
  return 1
}

# term_tree <pid> — graceful TERM of the process + its children, escalate to KILL. Guarded to repo pids.
term_tree() {
  local pid="$1" child i
  is_repo_pid "$pid" || return 0
  for child in $(pgrep -P "$pid" 2>/dev/null); do kill "$child" 2>/dev/null || true; done
  kill "$pid" 2>/dev/null || true
  for i in 1 2 3 4 5 6 7 8; do kill -0 "$pid" 2>/dev/null || return 0; sleep 0.5; done
  for child in $(pgrep -P "$pid" 2>/dev/null); do kill -9 "$child" 2>/dev/null || true; done
  kill -9 "$pid" 2>/dev/null || true
}

# rotate_log <name> — archive the previous run's log so history is never lost, prune to $LOG_KEEP.
rotate_log() {
  # NB: separate declarations — a single `local a=$1 b=$RUN_DIR/$a` expands $a before it is
  # assigned (bash evaluates all `local` args first), which trips `set -u` in our entrypoints.
  local name="$1" ts
  local log="$RUN_DIR/$name.log"
  [[ -s "$log" ]] || return 0
  ts="$(date +%Y%m%d-%H%M%S)"
  mv "$log" "$LOG_ARCHIVE/$name.$ts.log" 2>/dev/null || return 0
  ls -1t "$LOG_ARCHIVE/$name."*.log 2>/dev/null | tail -n +"$((LOG_KEEP + 1))" | while IFS= read -r old; do
    rm -f "$old" 2>/dev/null || true
  done
}

# stop_tracked <name> [port] — orphan-proof, isolation-safe stop of one app.
stop_tracked() {
  # Separate declarations so $name is assigned before $pidfile expands it (see rotate_log note; set -u).
  local name="$1" port="${2:-}" pid
  local pidfile="$RUN_DIR/$name.pid"
  if [[ -f "$pidfile" ]]; then
    pid="$(cat "$pidfile" 2>/dev/null)"
    is_repo_pid "$pid" && term_tree "$pid"
    rm -f "$pidfile"
  fi
  if [[ -n "$port" ]]; then
    pid="$(repo_listener "$port" || true)"
    [[ -n "$pid" ]] && term_tree "$pid"
  fi
  # Explicit success: a free port leaves the trailing `&&` at exit status 1, which would trip the
  # caller's `set -e` (start_tracked runs stop_tracked before every launch).
  return 0
}

# start_tracked <name> <port> -- <command...>
#   Refuses to clobber a FOREIGN port owner; replaces any existing repo instance (idempotent);
#   rotates the previous log; launches detached; records the pid.
start_tracked() {
  local name="$1" port="$2"; shift 2
  [[ "${1:-}" == "--" ]] && shift
  if [[ -n "$port" ]]; then
    local fp; fp="$(foreign_listener "$port" 2>/dev/null || true)"
    if [[ -n "$fp" ]]; then
      echo "  SKIP $name — port :$port held by a NON-repo process (pid $fp: $(ps -o command= -p "$fp" 2>/dev/null | head -c 60)); not clobbering another project." >&2
      return 1
    fi
  fi
  stop_tracked "$name" "$port"
  rotate_log "$name"
  nohup "$@" >"$RUN_DIR/$name.log" 2>&1 &
  local pid=$!
  echo "$pid" >"$RUN_DIR/$name.pid"; disown 2>/dev/null || true
  echo "started $name${port:+ (:$port)} (pid $pid) -> .run/$name.log"
}

# repo_process_inventory — human-readable list of this repo's live processes + which port each holds.
# Used by collect-logs.sh and doctor.sh; never kills anything.
repo_process_inventory() {
  echo "# repo processes (command references $REPO_ROOT)"
  ps -eo pid,ppid,etime,command | grep -F "$REPO_ROOT" \
    | grep -Eiv "claude|codegraph|OmniSharp|Roslyn|/procs.sh|grep -F" \
    | grep -Ei "bin/Debug/net10.0/3commerce\.|/node_modules/.bin/next|next-server|dotnet run --project" \
    | sed 's/^/  /'
}
