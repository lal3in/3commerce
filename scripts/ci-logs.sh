#!/usr/bin/env bash
# Triage the latest CI run for a branch/PR: its failing jobs + the error lines from each — the
# `gh run view --job <id> --log | strip-ansi | grep <signatures> | tail` ritual, automated.
# Usage: scripts/ci-logs.sh [branch]   (default: current branch)
set -uo pipefail
branch="${1:-$(git branch --show-current)}"
SIG='error|FAILED|failed to|did not complete|timed out|OOMKilled|no space|Back-off|CrashLoop|exit code|Unhealthy|context deadline|Insufficient|rpc error|Cannot connect|Conflict'

rid=$(gh run list --branch "$branch" --limit 1 --json databaseId --jq '.[0].databaseId' 2>/dev/null)
[[ -z "$rid" ]] && { echo "no CI runs for branch '$branch'"; exit 1; }
echo "run $rid · $(gh run view "$rid" --json status,conclusion --jq '.status+" / "+(.conclusion // "running")')"

mapfile -t failed < <(gh run view "$rid" --json jobs --jq '.jobs[]|select(.conclusion=="failure")|(.databaseId|tostring)+" "+.name' 2>/dev/null)
if ((${#failed[@]}==0)); then echo "no failing jobs."; exit 0; fi

for line in "${failed[@]}"; do
  jid="${line%% *}"; jname="${line#* }"
  echo; echo "════ $jname ════"
  gh run view --job "$jid" --log 2>&1 | sed 's/\x1b\[[0-9;]*m//g' \
    | grep -iE "$SIG" | grep -viE 'no error|0 error|errorlevel' | tail -15 | sed 's/^/  /'
done
