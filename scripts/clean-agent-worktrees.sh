#!/usr/bin/env bash
# clean-agent-worktrees.sh — remove finished subagent worktrees (.claude/worktrees/agent-*) and the
# local branches they leave behind. Runs automatically after every merge (scripts/pr-merge-on-green.sh)
# and at session start (.claude/settings.json SessionStart hook); safe to run by hand any time.
#
#   scripts/clean-agent-worktrees.sh            # remove what's finished, print what was removed/kept
#   DRY_RUN=1 scripts/clean-agent-worktrees.sh  # only print
#   QUIET=1 …                                    # print removals only (hook mode)
#
# A worktree is removed ONLY on positive evidence its work is finished — never because it looks idle:
#   - its working tree is clean (no modified or untracked files; ignored build output is fine), AND
#   - its branch tip is the head of a MERGED PR (the work landed, nothing was added after), OR
#     its tip is already in develop's history and the worktree has been idle > $STALE_HOURS (an agent
#     that finished without committing anything).
# Agent worktrees are locked by the Claude Code process that created them for the whole session, so the
# lock says nothing about whether the agent is still working — a running agent has no merged PR yet and
# isn't idle, so it is always kept.
#
# Then deletes local branches that no worktree uses: `worktree-agent-*` branches already in develop, and
# any branch whose tip is the head of a merged PR. Never touches main, develop, test or the current branch.
set -euo pipefail

DRY_RUN="${DRY_RUN:-0}"
QUIET="${QUIET:-0}"
STALE_HOURS="${STALE_HOURS:-24}"

root="$(git rev-parse --show-toplevel)"
cd "$root"
say()  { [[ "$QUIET" == "1" ]] || echo "$*"; }
act()  { echo "$*"; }

git fetch -q --prune origin 2>/dev/null || true
base="origin/develop"
git rev-parse -q --verify "$base" >/dev/null || base="origin/main"

have_gh=0
command -v gh >/dev/null && gh auth status >/dev/null 2>&1 && have_gh=1

# Heads of merged PRs for a branch name (one per line); empty without gh.
merged_heads() { (( have_gh )) && gh pr list --head "$1" --state merged --limit 20 --json headRefOid -q '.[].headRefOid' 2>/dev/null || true; }

mtime() { stat -f %m "$1" 2>/dev/null || stat -c %Y "$1" 2>/dev/null || echo 0; }

# done_reason <branch> <tip> [<worktree>]  → prints why the work is finished, or nothing.
done_reason() {
  local b="$1" tip="$2" wt="${3:-}"
  if [[ -n "$b" ]] && merged_heads "$b" | grep -qx "$tip"; then echo "PR merged at ${tip:0:7}"; return; fi
  if git merge-base --is-ancestor "$tip" "$base" 2>/dev/null; then
    if [[ -z "$wt" ]]; then echo "already in ${base#origin/}"; return; fi
    local gitdir newest=0 t f
    gitdir="$(git -C "$wt" rev-parse --absolute-git-dir)"
    for f in "$gitdir/index" "$gitdir/HEAD" "$gitdir/logs/HEAD" "$wt"; do
      t="$(mtime "$f")"; (( t > newest )) && newest=$t
    done
    if (( $(date +%s) - newest > STALE_HOURS * 3600 )); then echo "no new commits, idle > ${STALE_HOURS}h"; fi
  fi
}

current="$(git branch --show-current)"
protected="main develop test $current"
removed=0 kept=0

# 1. Worktrees.
while IFS= read -r line; do
  wt="${line#worktree }"
  [[ "$wt" == "$root/.claude/worktrees/agent-"* ]] || continue
  branch="$(git -C "$wt" branch --show-current 2>/dev/null || true)"
  tip="$(git -C "$wt" rev-parse HEAD 2>/dev/null || true)"
  name="${wt#$root/}"
  if [[ -n "$(git -C "$wt" status --porcelain 2>/dev/null)" ]]; then
    say "keep   $name (${branch:-detached}): uncommitted changes"; kept=$((kept+1)); continue
  fi
  reason="$(done_reason "${branch:-}" "$tip" "$wt")"
  if [[ -z "$reason" ]]; then
    say "keep   $name (${branch:-detached}): work not merged yet"; kept=$((kept+1)); continue
  fi
  if [[ "$DRY_RUN" == "1" ]]; then act "would remove $name (${branch:-detached}): $reason"; continue; fi
  git worktree unlock "$wt" 2>/dev/null || true
  # --force only drops ignored build output (bin/obj/node_modules): tracked + untracked were checked clean above.
  git worktree remove --force "$wt"
  act "removed $name (${branch:-detached}): $reason"; removed=$((removed+1))
done < <(git worktree list --porcelain | grep '^worktree ')
git worktree prune

# 2. Local branches no worktree uses.
in_use="$(git worktree list --porcelain | sed -n 's#^branch refs/heads/##p')"
for b in $(git for-each-ref --format='%(refname:short)' refs/heads/); do
  [[ " $protected " == *" $b "* ]] && continue
  grep -qx "$b" <<<"$in_use" && continue
  tip="$(git rev-parse "refs/heads/$b")"
  reason=""
  if [[ "$b" == worktree-agent-* ]]; then
    reason="$(done_reason "$b" "$tip")"
  elif merged_heads "$b" | grep -qx "$tip"; then
    reason="PR merged at ${tip:0:7}"
  fi
  [[ -z "$reason" ]] && continue
  if [[ "$DRY_RUN" == "1" ]]; then act "would delete branch $b: $reason"; continue; fi
  git branch -D "$b" >/dev/null
  act "deleted branch $b: $reason"; removed=$((removed+1))
done

say "clean-agent-worktrees: $removed removed, $kept worktree(s) kept"
