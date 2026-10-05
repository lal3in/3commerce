#!/usr/bin/env bash
# pr-merge-on-green.sh — squash-merge a feature/defect PR into `develop` as soon as its REQUIRED CI
# gates are green (ADR-0058). PRs against `test`/`main` are refused: those stages move only by
# fast-forward promotion (`scripts/promote.sh`).
#
#   scripts/pr-merge-on-green.sh <pr-number>
#
# Run it in the background (an agent: Bash run_in_background, never a shell `&`, so the
# completion notification arrives). It only talks to GitHub — it never runs git checkout/pull,
# so it can't switch the branch of the working tree you're editing in.
#
# Behaviour:
#   - polls `gh pr checks` every $INTERVAL seconds until every gate in $REQUIRED passes, then
#     `gh pr merge --squash` (never --admin — the required gates are the contract) and deletes the
#     remote head branch via the API (not --delete-branch, which touches the local checkout);
#   - when a required gate FAILS: waits for the whole workflow run to reach status=completed
#     (`gh run rerun` is refused while any job is still running), then reruns its failed jobs.
#     Only reruns GitHub accepted count toward $MAX_RERUNS. A required gate that keeps failing is
#     a real failure until proven otherwise — the script stops and you read the log
#     (`scripts/ci-logs.sh <branch>`);
#   - non-required jobs (docker, compose-smoke, kind-deploy) don't block the merge into develop,
#     but any that are red at merge time are listed — they DO block promotion to test, so fix them.
#
# Env: REQUIRED (default "changes build-test integration browser-e2e" — the develop ruleset),
#      INTERVAL (60), MAX_RERUNS (3),
#      TIMEOUT_MIN (120).
# Exit: 0 merged (or already merged) · 1 failed/closed/timed out · 2 usage.
set -euo pipefail

PR="${1:-}"
[[ "$PR" =~ ^[0-9]+$ ]] || { echo "usage: $0 <pr-number>" >&2; exit 2; }

REQUIRED="${REQUIRED:-changes build-test integration browser-e2e}"
INTERVAL="${INTERVAL:-60}"
MAX_RERUNS="${MAX_RERUNS:-3}"
TIMEOUT_MIN="${TIMEOUT_MIN:-120}"

log() { echo "[$(date +%H:%M:%S)] PR #$PR: $*"; }

base="$(gh pr view "$PR" --json baseRefName -q .baseRefName)"
if [[ "$base" != "develop" ]]; then
  log "REFUSED — base is '$base'. Feature/defect PRs target develop; test and main move only by promotion (scripts/promote.sh, ADR-0058)."
  exit 1
fi

deadline=$(( $(date +%s) + TIMEOUT_MIN * 60 ))
reruns=0
waiting_on_run=""

while :; do
  (( $(date +%s) > deadline )) && { log "timed out after ${TIMEOUT_MIN}m"; exit 1; }

  state="$(gh pr view "$PR" --json state -q .state)"
  case "$state" in
    MERGED) log "already merged"; exit 0 ;;
    CLOSED) log "PR is closed — nothing to merge"; exit 1 ;;
  esac

  # One line per check: <bucket>\t<name>\t<link>
  checks="$(gh pr checks "$PR" --json name,bucket,link -q '.[] | [.bucket, .name, .link] | @tsv' 2>/dev/null || true)"
  if [[ -z "$checks" ]]; then
    log "no checks reported yet"; sleep "$INTERVAL"; continue
  fi

  pending=() failed=() failed_runs=()
  for gate in $REQUIRED; do
    row="$(printf '%s\n' "$checks" | awk -F'\t' -v g="$gate" '$2 == g' | head -1)"
    bucket="${row%%$'\t'*}"
    case "$bucket" in
      pass|skipping) ;;
      fail|cancel)
        failed+=("$gate")
        run_id="$(printf '%s' "$row" | sed -nE 's#.*/actions/runs/([0-9]+)/.*#\1#p')"
        [[ -n "$run_id" ]] && failed_runs+=("$run_id")
        ;;
      *) pending+=("${gate}${bucket:+:$bucket}") ;;
    esac
  done

  if (( ${#failed[@]} > 0 )); then
    run_id="${failed_runs[0]:-}"
    [[ -n "$run_id" ]] || { log "required gate(s) failed: ${failed[*]} (no run id to rerun)"; exit 1; }
    run_status="$(gh run view "$run_id" --json status -q .status)"
    if [[ "$run_status" != "completed" ]]; then
      [[ "$waiting_on_run" != "$run_id" ]] && log "required gate(s) failed: ${failed[*]} — waiting for run $run_id to complete before rerunning"
      waiting_on_run="$run_id"; sleep "$INTERVAL"; continue
    fi
    if (( reruns >= MAX_RERUNS )); then
      log "required gate(s) still failing after $reruns rerun(s): ${failed[*]} — treat as a real failure: scripts/ci-logs.sh"
      exit 1
    fi
    if gh run rerun "$run_id" --failed >/dev/null 2>&1; then
      reruns=$(( reruns + 1 ))
      log "rerunning failed jobs of run $run_id (${failed[*]}) — attempt $reruns/$MAX_RERUNS"
    else
      log "rerun of $run_id refused — retrying next poll"
    fi
    waiting_on_run=""; sleep "$INTERVAL"; continue
  fi

  if (( ${#pending[@]} > 0 )); then
    sleep "$INTERVAL"; continue
  fi

  # All required gates green.
  red_optional="$(printf '%s\n' "$checks" | awk -F'\t' '$1 == "fail" || $1 == "cancel" { print $2 }' | paste -sd, - || true)"
  head_branch="$(gh pr view "$PR" --json headRefName -q .headRefName)"
  log "required gates green (${REQUIRED// /, }) — squash-merging"
  gh pr merge "$PR" --squash
  repo="$(gh repo view --json nameWithOwner -q .nameWithOwner)"
  gh api -X DELETE "repos/$repo/git/refs/heads/$head_branch" >/dev/null 2>&1 \
    && log "deleted remote branch $head_branch" || log "remote branch $head_branch not deleted (already gone?)"
  [[ -n "$red_optional" ]] && log "MERGED, but non-required checks are red: $red_optional — fix them as a follow-up"
  log "merged into develop. Your local checkout is untouched. When develop is green on every gate, promote: scripts/promote.sh test"
  exit 0
done
