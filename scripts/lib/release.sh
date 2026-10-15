#!/usr/bin/env bash
# Release identity (ADR-0064) — the ONE place scripts derive what they stamp into builds and images.
# The release IS the commit (ADR-0058 promotes one tested SHA), so:
#   release_revision  → HEAD's full SHA, "-dirty" appended when tracked files differ from HEAD
#                       (untracked files don't count). Empty outside a git checkout.
#   release_version   → the product version: $VERSION if set, else <VersionPrefix> from Directory.Build.props.
# Consumers: `GIT_SHA="$(release_revision)" dotnet build …` (Directory.Build.props turns GIT_SHA into
# SourceRevisionId), and `--build-arg GIT_SHA=… --build-arg VERSION=…` for images (scripts/build-images.sh,
# scripts/launch.sh via compose build args). Services read it back into service.version + vcs.ref.head.revision.
# Usage: source scripts/lib/release.sh   (paths are relative to the repo root)

release_revision() {
  local sha
  sha="$(git rev-parse HEAD 2>/dev/null)" || { echo ""; return 0; }
  if [[ -n "$(git status --porcelain --untracked-files=no 2>/dev/null)" ]]; then
    echo "${sha}-dirty"
  else
    echo "$sha"
  fi
}

release_version() {
  if [[ -n "${VERSION:-}" ]]; then echo "$VERSION"; return 0; fi
  sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' Directory.Build.props | head -1
}
