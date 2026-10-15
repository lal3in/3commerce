#!/usr/bin/env bash
# Build container images with bounded concurrency so 13 parallel .NET builds can't OOM the Docker VM.
# Discovers Dockerfiles (no hardcoded list) and builds PARALLEL at a time (default 2).
# Usage: PARALLEL=2 scripts/build-images.sh [image-name ...]    e.g. scripts/build-images.sh catalog gateway
#        (no names = every image; names are image_name() values: catalog, notifications, supplier-portal, migrator…)
# Every image is stamped with its release (ADR-0064): --build-arg GIT_SHA (HEAD, "-dirty" for uncommitted tracked
# changes) + VERSION (Directory.Build.props VersionPrefix), both overridable from the environment. The .NET images
# report them as service.version / vcs.ref.head.revision; every image carries them as OCI labels
# (org.opencontainers.image.revision / .version / .source) — check with `docker inspect 3commerce/<name>:local`.
# Maintain: image_name() must map every Dockerfile path to its compose/Helm image name.
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/lib/preflight.sh
source scripts/lib/release.sh
require_docker
require_docker_memory "${MIN_GIB:-8}"
export DOCKER_BUILDKIT=1
PARALLEL="${PARALLEL:-2}"
GIT_SHA="${GIT_SHA:-$(release_revision)}"
VERSION="$(release_version)"

image_name() { # derive the compose/Helm image name from a Dockerfile path
  case "$1" in
    src/Services/*/Api/Dockerfile) echo "$1" | cut -d/ -f3 | tr '[:upper:]' '[:lower:]' ;;
    src/Workers/*/Dockerfile)      echo "$1" | cut -d/ -f3 | tr '[:upper:]' '[:lower:]' ;;  # Notifications -> notifications
    deploy/*/Dockerfile)           echo "$1" | cut -d/ -f2 ;;
    src/*/Dockerfile)              echo "$1" | cut -d/ -f2 | sed -E 's/([a-z])([A-Z])/\1-\2/g' | tr '[:upper:]' '[:lower:]' ;;  # SupplierPortal -> supplier-portal
  esac
}

mapfile -t ALL < <(find src deploy -name Dockerfile | sort)
DFS=()
if (( $# )); then
  for want in "$@"; do
    found=""
    for df in "${ALL[@]}"; do [[ "$(image_name "$df")" == "$want" ]] && { DFS+=("$df"); found=1; }; done
    if [[ -z "$found" ]]; then
      echo "unknown image '$want' — known: $(for df in "${ALL[@]}"; do printf '%s ' "$(image_name "$df")"; done)" >&2
      exit 1
    fi
  done
else
  DFS=( "${ALL[@]}" )
fi

echo "Building ${#DFS[@]} image(s), ${PARALLEL} at a time — release ${VERSION}+${GIT_SHA:-unknown}..."
i=0
for df in "${DFS[@]}"; do
  name="$(image_name "$df")"
  ( docker build -q -f "$df" -t "3commerce/$name:local" \
      --build-arg "GIT_SHA=$GIT_SHA" --build-arg "VERSION=$VERSION" . >/dev/null && echo "  built $name" ) &
  (( ++i % PARALLEL == 0 )) && wait
done
wait
echo "done"
