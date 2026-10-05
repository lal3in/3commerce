#!/usr/bin/env bash
# promote.sh — fast-forward a release stage to the exact commit the previous stage tested (ADR-0058).
#
#   scripts/promote.sh test                       # develop → test
#   scripts/promote.sh main --user-approved       # test → main (production) — ONLY after the user said yes
#
# What it guarantees:
#   - fast-forward only: the target must be an ancestor of the source, so no new commit is ever
#     created during a promotion — what passed the earlier stage is byte-for-byte what moves on;
#   - every gate the target stage requires is green ON THAT COMMIT (check runs are per commit, so
#     the results travel with it; GitHub's branch rules re-check the same thing on push);
#   - production needs an explicit --user-approved: agents must ask the user before every release;
#   - it only talks to the remote (git push <sha>:refs/heads/<target>) — your working tree and
#     local branches are never touched.
#
# Env: DRY_RUN=1 prints what would be promoted and stops before the push.
# Exit: 0 promoted (or nothing to promote) · 1 refused · 2 usage.
set -euo pipefail

usage() { echo "usage: $0 test | main --user-approved" >&2; exit 2; }
target="${1:-}"; shift || true
approved=0
for a in "$@"; do [[ "$a" == "--user-approved" ]] && approved=1 || usage; done

case "$target" in
  test) source=develop ;;
  main) source=test
        (( approved )) || { echo "promote: production release needs --user-approved — ask the user first, every time." >&2; exit 1; } ;;
  *) usage ;;
esac

# Gates required on the commit before it may enter each stage (keep in sync with the GitHub rulesets).
REQUIRED="changes build-test integration browser-e2e compose-smoke kind-deploy"

git fetch -q origin "$source" "$target"
src_sha="$(git rev-parse "origin/$source")"
dst_sha="$(git rev-parse "origin/$target")"

if [[ "$src_sha" == "$dst_sha" ]]; then
  echo "promote: $target is already at $source (${src_sha:0:7}) — nothing to promote."; exit 0
fi
if ! git merge-base --is-ancestor "$dst_sha" "$src_sha"; then
  echo "promote: REFUSED — $target (${dst_sha:0:7}) is not an ancestor of $source (${src_sha:0:7})." >&2
  echo "         $target has commits $source doesn't; a fast-forward is impossible. Investigate — never force-push a stage." >&2
  exit 1
fi

repo="$(gh repo view --json nameWithOwner -q .nameWithOwner)"
# Latest run per check name on the source commit: <name>\t<status>\t<conclusion>
runs="$(gh api --paginate "repos/$repo/commits/$src_sha/check-runs?per_page=100" \
  -q '.check_runs[] | [.name, .status, (.conclusion // ""), .started_at] | @tsv' \
  | sort -t$'\t' -k1,1 -k4,4r | awk -F'\t' '!seen[$1]++')"

bad=()
for gate in $REQUIRED; do
  row="$(printf '%s\n' "$runs" | awk -F'\t' -v g="$gate" '$1 == g')"
  status="$(cut -f2 <<<"$row")"; conclusion="$(cut -f3 <<<"$row")"
  if [[ -z "$row" ]]; then bad+=("$gate: no run on ${src_sha:0:7}")
  elif [[ "$status" != "completed" ]]; then bad+=("$gate: still $status")
  elif [[ "$conclusion" != "success" && "$conclusion" != "skipped" && "$conclusion" != "neutral" ]]; then bad+=("$gate: $conclusion")
  fi
done
if (( ${#bad[@]} > 0 )); then
  echo "promote: REFUSED — ${source}@${src_sha:0:7} is not green for $target:" >&2
  printf '  - %s\n' "${bad[@]}" >&2
  exit 1
fi

echo "promote: $source → $target  ${dst_sha:0:7}..${src_sha:0:7}  (all gates green: ${REQUIRED// /, })"
git log --format='  %h %s' "$dst_sha..$src_sha" | head -40
[[ "${DRY_RUN:-}" == "1" ]] && { echo "promote: DRY_RUN — not pushing."; exit 0; }

git push origin "$src_sha:refs/heads/$target"
echo "promote: $target is now ${src_sha:0:7}."
