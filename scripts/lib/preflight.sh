# Preflight helpers so a too-small Docker VM fails FAST with guidance, not by crashing mid-build.
require_docker() {
  docker info >/dev/null 2>&1 || { echo "ERROR: Docker is not responding. Start it (e.g. 'colima start')." >&2; exit 1; }
}

# require_docker_memory <min_gib>  — warns/aborts when the Docker VM has too little RAM for image builds.
require_docker_memory() {
  local need_gib="${1:-8}" mem_bytes mem_gib
  mem_bytes="$(docker info --format '{{.MemTotal}}' 2>/dev/null || echo 0)"
  mem_gib=$(( mem_bytes / 1024 / 1024 / 1024 ))
  if (( mem_gib < need_gib )); then
    echo "ERROR: Docker VM has ${mem_gib} GiB; image builds need ~${need_gib} GiB (13 .NET builds OOM a small VM)." >&2
    echo "  Fix: 'colima stop && colima start --cpu 4 --memory ${need_gib}' — or use bare-run: scripts/dev-up.sh" >&2
    exit 1
  fi
  echo "Docker VM memory: ${mem_gib} GiB (ok)"
}
