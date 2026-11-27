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
    echo "ERROR: Docker VM has ${mem_gib} GiB; image builds need ~${need_gib} GiB (17 .NET builds OOM a small VM)." >&2
    echo "  Fix: 'colima stop && colima start --cpu 4 --memory ${need_gib}' — or use bare-run: scripts/dev-up.sh" >&2
    exit 1
  fi
  echo "Docker VM memory: ${mem_gib} GiB (ok)"
}

# require_storefront_deps [dir]  — the storefront's package manager is pnpm (ADR-0070), at the version
# src/Storefront/package.json "packageManager" pins. Fails fast with install guidance when pnpm is missing, and
# installs the locked deps when node_modules is absent or was laid out by npm (no node_modules/.modules.yaml —
# pnpm would otherwise stop to ask before purging it).
require_storefront_deps() {
  local dir="${1:-src/Storefront}" want
  want="$(sed -n 's/.*"packageManager": *"pnpm@\([^"]*\)".*/\1/p' "$dir/package.json")"
  if ! command -v pnpm >/dev/null 2>&1; then
    echo "ERROR: pnpm is not installed — the storefront uses pnpm ${want:-?} (ADR-0070)." >&2
    echo "  Install it: 'npm install -g pnpm@${want:-latest}' (or 'corepack enable pnpm' on a Node that ships corepack)." >&2
    exit 1
  fi
  if [[ ! -f "$dir/node_modules/.modules.yaml" ]]; then
    echo "  storefront deps missing or npm-installed — pnpm install --frozen-lockfile in $dir"
    rm -rf "$dir/node_modules"
    ( cd "$dir" && pnpm install --frozen-lockfile >/dev/null ) || { echo "ERROR: pnpm install failed in $dir" >&2; exit 1; }
  fi
}
