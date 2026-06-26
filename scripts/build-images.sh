#!/usr/bin/env bash
# Build all container images with bounded concurrency so 13 parallel .NET builds can't OOM the Docker VM.
# Discovers Dockerfiles (no hardcoded list) and builds PARALLEL at a time (default 2).
# Usage: PARALLEL=2 scripts/build-images.sh
# Maintain: image_name() must map every Dockerfile path to its compose/Helm image name.
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/lib/preflight.sh
require_docker
require_docker_memory "${MIN_GIB:-8}"
export DOCKER_BUILDKIT=1
PARALLEL="${PARALLEL:-2}"

mapfile -t DFS < <(find src deploy -name Dockerfile | sort)
echo "Building ${#DFS[@]} images, ${PARALLEL} at a time..."
image_name() { # derive the compose/Helm image name from a Dockerfile path
  case "$1" in
    src/Services/*/Api/Dockerfile) echo "$1" | cut -d/ -f3 | tr '[:upper:]' '[:lower:]' ;;
    src/Workers/*/Dockerfile)      echo "$1" | cut -d/ -f3 | tr '[:upper:]' '[:lower:]' ;;  # Notifications -> notifications
    deploy/*/Dockerfile)           echo "$1" | cut -d/ -f2 ;;
    src/*/Dockerfile)              echo "$1" | cut -d/ -f2 | sed -E 's/([a-z])([A-Z])/\1-\2/g' | tr '[:upper:]' '[:lower:]' ;;  # SupplierPortal -> supplier-portal
  esac
}
i=0
for df in "${DFS[@]}"; do
  name="$(image_name "$df")"
  ( docker build -q -f "$df" -t "3commerce/$name:local" . >/dev/null && echo "  built $name" ) &
  (( ++i % PARALLEL == 0 )) && wait
done
wait
echo "done"
