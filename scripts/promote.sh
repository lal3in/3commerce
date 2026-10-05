#!/usr/bin/env bash
# promote.sh — fast-forward a release stage to the exact commit the previous stage tested (ADR-0058).
#
#   scripts/promote.sh test [--wait]                       # develop → test
#   scripts/promote.sh main --user-approved [--wait]       # test → main (production) — ONLY after the user said yes
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
# --wait: instead of refusing while gates are still queued/running (or not created yet — a just-pushed
#   commit's check runs can take a minute to appear), poll the latest run per gate on the source
#   commit until every gate has completed, printing one line each time the pending set changes.
#   Then the normal rules apply (any gate not success/skipped/neutral → refused). After the wait the
#   source tip and fast-forwardability are re-checked: if origin/<source> moved, it refuses (re-run) —
#   only the commit whose gates were checked is ever promoted.
#
# Env: DRY_RUN=1 prints what would be promoted and stops before the push (with --wait it still waits).
#      WAIT_TIMEOUT_MIN (default 60) · WAIT_INTERVAL seconds between polls (default 30) — --wait only.
# Exit: 0 promoted (or nothing to promote) · 1 refused (red gate, not ff, timeout, source moved) · 2 usage.
# bash 3.2 compatible (macOS /bin/bash): no associative arrays, no empty-array expansion under set -u.
set -euo pipefail

usage() { echo "usage: $0 test [--wait] | main --user-approved [--wait]   (env: DRY_RUN=1 WAIT_TIMEOUT_MIN=60 WAIT_INTERVAL=30)" >&2; exit 2; }
target="${1:-}"; shift || true
approved=0; wait=0
for a in "$@"; do
  case "$a" in
    --user-approved) approved=1 ;;
    --wait) wait=1 ;;
    *) usage ;;
  esac
done

case "$target" in
  test) source=develop ;;
  main) source=test
        (( approved )) || { echo "promote: production release needs --user-approved — ask the user first, every time." >&2; exit 1; } ;;
  *) usage ;;
esac

WAIT_TIMEOUT_MIN="${WAIT_TIMEOUT_MIN:-60}"
WAIT_INTERVAL="${WAIT_INTERVAL:-30}"
[[ "$WAIT_TIMEOUT_MIN" =~ ^[0-9]+$ ]] || { echo "promote: WAIT_TIMEOUT_MIN must be a whole number of minutes" >&2; usage; }
[[ "$WAIT_INTERVAL" =~ ^[1-9][0-9]*$ ]] || { echo "promote: WAIT_INTERVAL must be a positive number of seconds" >&2; usage; }

# Gates required on the commit before it may enter each stage (keep in sync with the GitHub rulesets).
REQUIRED="changes build-test integration browser-e2e compose-smoke kind-deploy"

git fetch -q origin "$source" "$target"
src_sha="$(git rev-parse "origin/$source")"
dst_sha="$(git rev-parse "origin/$target")"

# Fast-forward check (exit 0 if nothing to do, exit 1 if impossible). Run before and after any wait.
check_ff() {
  if [[ "$src_sha" == "$dst_sha" ]]; then
    echo "promote: $target is already at $source (${src_sha:0:7}) — nothing to promote."; exit 0
  fi
  if ! git merge-base --is-ancestor "$dst_sha" "$src_sha"; then
    echo "promote: REFUSED — $target (${dst_sha:0:7}) is not an ancestor of $source (${src_sha:0:7})." >&2
    echo "         $target has commits $source doesn't; a fast-forward is impossible. Investigate — never force-push a stage." >&2
    exit 1
  fi
}
check_ff

repo="$(gh repo view --json nameWithOwner -q .nameWithOwner)"

# Latest run per check name on the source commit: <name>\t<status>\t<conclusion>\t<started_at>
fetch_runs() {
  gh api --paginate "repos/$repo/commits/$src_sha/check-runs?per_page=100" \
    -q '.check_runs[] | [.name, .status, (.conclusion // ""), .started_at] | @tsv' \
    | sort -t$'\t' -k1,1 -k4,4r | awk -F'\t' '!seen[$1]++'
}

# Classify every gate from $runs into newline-separated lists:
#   pending_names — gates with no run yet or not completed (just the names; the progress-change key)
#   pending       — the same, with detail ("browser-e2e: still in_progress", "kind-deploy: no run on abc1234")
#   failed        — completed gates whose conclusion isn't success/skipped/neutral
classify() {
  pending_names=""; pending=""; failed=""
  local gate row status conclusion
  for gate in $REQUIRED; do
    row="$(printf '%s\n' "$runs" | awk -F'\t' -v g="$gate" '$1 == g')"
    status="$(cut -f2 <<<"$row")"; conclusion="$(cut -f3 <<<"$row")"
    if [[ -z "$row" ]]; then
      pending_names+="$gate "; pending+="$gate: no run on ${src_sha:0:7}"$'\n'
    elif [[ "$status" != "completed" ]]; then
      pending_names+="$gate "; pending+="$gate: still $status"$'\n'
    elif [[ "$conclusion" != "success" && "$conclusion" != "skipped" && "$conclusion" != "neutral" ]]; then
      failed+="$gate: $conclusion"$'\n'
    fi
  done
}

refuse_gates() {  # $1 = heading, $2 = newline-separated reasons
  echo "promote: REFUSED — ${source}@${src_sha:0:7} $1" >&2
  printf '%s' "$2" | sed '/^$/d; s/^/  - /' >&2
  exit 1
}

if (( ! wait )); then
  runs="$(fetch_runs)"
  classify
  if [[ -n "$pending$failed" ]]; then refuse_gates "is not green for $target:" "$pending$failed"; fi
else
  deadline=$(( WAIT_TIMEOUT_MIN * 60 )); SECONDS=0; last_key="<none>"
  echo "promote: --wait — polling ${source}@${src_sha:0:7}'s gates every ${WAIT_INTERVAL}s (timeout ${WAIT_TIMEOUT_MIN}m)."
  while :; do
    if runs="$(fetch_runs)"; then
      classify
      if [[ -z "$pending_names" ]]; then break; fi
      if [[ "$pending_names" != "$last_key" ]]; then
        last_key="$pending_names"
        detail="$(printf '%s' "$pending" | sed '/^$/d' | sed 's/: no run on [0-9a-f]*/ (no run yet)/; s/: still \(.*\)/ (\1)/' | paste -sd, - | sed 's/,/, /g')"
        msg="promote: [$(( SECONDS / 60 ))m$(( SECONDS % 60 ))s] waiting on $(wc -w <<<"$pending_names" | tr -d ' ') gate(s): $detail"
        if [[ -n "$failed" ]]; then msg+="  — already red: $(printf '%s' "$failed" | sed '/^$/d' | paste -sd, - | sed 's/,/, /g')"; fi
        echo "$msg"
      fi
    else
      echo "promote: [$(( SECONDS / 60 ))m$(( SECONDS % 60 ))s] couldn't read check runs (gh failed) — retrying." >&2
      if [[ -z "${pending+x}" ]]; then pending_names="$REQUIRED"; pending="(check runs unreadable)"$'\n'; failed=""; fi
    fi
    if (( SECONDS >= deadline )); then
      refuse_gates "— timed out after ${WAIT_TIMEOUT_MIN}m waiting for $target's gates; still pending:" "$pending$failed"
    fi
    left=$(( deadline - SECONDS )); nap=$WAIT_INTERVAL
    if (( left < nap )); then nap=$left; fi
    sleep "$nap"
  done
  echo "promote: all gates completed after $(( SECONDS / 60 ))m$(( SECONDS % 60 ))s."
  if [[ -n "$failed" ]]; then refuse_gates "is not green for $target:" "$failed"; fi

  # The wait took time: re-read both stages. Never promote a commit other than the one checked.
  git fetch -q origin "$source" "$target"
  now_src="$(git rev-parse "origin/$source")"
  if [[ "$now_src" != "$src_sha" ]]; then
    echo "promote: REFUSED — origin/$source moved during the wait (${src_sha:0:7} → ${now_src:0:7})." >&2
    echo "         The gates checked belong to ${src_sha:0:7}; re-run promote.sh to check the new tip." >&2
    exit 1
  fi
  dst_sha="$(git rev-parse "origin/$target")"
  check_ff
fi

echo "promote: $source → $target  ${dst_sha:0:7}..${src_sha:0:7}  (all gates green: ${REQUIRED// /, })"
git log -n 40 --format='  %h %s' "$dst_sha..$src_sha"   # -n, not | head: under pipefail a SIGPIPE'd git log would abort
[[ "${DRY_RUN:-}" == "1" ]] && { echo "promote: DRY_RUN — not pushing (would push ${src_sha:0:7} to refs/heads/$target)."; exit 0; }

git push origin "$src_sha:refs/heads/$target"
echo "promote: $target is now ${src_sha:0:7}."
