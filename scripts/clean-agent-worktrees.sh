#!/usr/bin/env bash
# clean-agent-worktrees.sh — remove finished subagent worktrees (.claude/worktrees/agent-*) and the
# local branches they leave behind. Runs automatically after every merge (scripts/pr-merge-on-green.sh)
# and at session start (.claude/settings.json SessionStart hook); safe to run by hand any time, from the
# main checkout or from any worktree (it always operates on the main checkout's worktree list).
#
#   scripts/clean-agent-worktrees.sh            # remove what's finished, print what was removed/kept
#   DRY_RUN=1 scripts/clean-agent-worktrees.sh  # only print (changes nothing, not even `worktree prune`)
#   QUIET=1 …                                    # print removals only (hook mode)
#
# A worktree is removed ONLY on positive evidence its work is finished — never because it looks idle.
# Its working tree must be clean (no modified or untracked files; ignored build output is fine), AND one of:
#   1. its branch tip is the head of a MERGED PR for that branch name (the work landed, nothing added after);
#   2. its tip has >= 1 commit not in develop's history, AND merging the tip into develop changes nothing
#      (`git merge-tree --write-tree origin/develop <tip>` succeeds with a tree equal to develop's tree) —
#      every change on the branch is already in develop. This catches squash-merged work whose tip is NOT
#      the merged PR head: an intermediate commit of the PR, or a `worktree-agent-*` branch whose commits
#      were pushed to a PR branch via `git push origin HEAD:<pr-branch>` (no PR has its branch name);
#   3. its tip has >= 1 commit not in develop's history, AND GitHub lists the tip commit among the commits
#      of a PR MERGED into develop (`gh api repos/{owner}/{repo}/commits/<sha>/pulls`). This covers rule 2's
#      blind spot: when develop later edited the same lines, the merge conflicts — and a conflict is NEVER
#      treated as finished on its own (no gh, or no merged PR → kept);
#   4. its tip is already in develop's history (0 commits ahead) and the worktree has been idle
#      > $STALE_HOURS (an agent that finished without committing anything).
#
# Why a running agent is never removed:
#   - Mid-task, its new commits are NOT in develop, so the merge in rule 2 changes develop's tree (kept), and
#     its tip is unpushed or in an OPEN PR (rule 3 sees no merged PR → kept); an uncommitted edit fails the
#     clean check outright.
#   - A freshly started agent sits at develop's tip with 0 commits of its own. Rules 2 and 3 both require
#     >= 1 commit ahead, so they never fire for it — this matters for rule 3 especially, because GitHub
#     associates develop's own squash commits with the PR that produced them. Only rule 4 applies, and
#     only after $STALE_HOURS of inactivity.
#   - Rules 2 and 3 lose nothing even in the worst case: rule 2's condition means develop already contains
#     every change on the branch, and rule 3's means the commit is preserved on GitHub in a merged PR.
#     (The one theoretical false positive — an agent whose commits net to zero against develop, e.g. a
#     commit plus its revert — still loses no content.)
# Agent worktrees are locked by the Claude Code process that created them for the whole session, so the
# lock says nothing about whether the agent is still working.
#
# Then deletes local branches that no worktree uses when rule 1, 2 or 3 holds (rule 1 checked against
# the branch's own name), or — for `worktree-agent-*` branches only — the tip is already in develop.
# Never touches main, develop, test or the current branch. Rule 2 needs git >= 2.38 (merge-tree
# --write-tree); on older git it never fires. Rules 1 and 3 need an authenticated `gh`; without it they
# never fire.
set -euo pipefail

DRY_RUN="${DRY_RUN:-0}"
QUIET="${QUIET:-0}"
STALE_HOURS="${STALE_HOURS:-24}"

# The main checkout (first entry of the worktree list), so running from inside a worktree works too.
root="$(git worktree list --porcelain | sed -n '1s/^worktree //p')"
cd "$root"
say()  { [[ "$QUIET" == "1" ]] || echo "$*"; }
act()  { echo "$*"; }

git fetch -q --prune origin 2>/dev/null || true
base="origin/develop"
git rev-parse -q --verify "$base" >/dev/null || base="origin/main"
base_name="${base#origin/}"
base_tree="$(git rev-parse "$base^{tree}")"

have_gh=0
command -v gh >/dev/null && gh auth status >/dev/null 2>&1 && have_gh=1

# Heads of merged PRs for a branch name (one per line); empty without gh.
merged_heads() { (( have_gh )) && gh pr list --head "$1" --state merged --limit 20 --json headRefOid -q '.[].headRefOid' 2>/dev/null || true; }

# Number of a PR merged into $base that contains commit <sha>; empty without gh or when there is none.
merged_pr_of_commit() {
  (( have_gh )) || return 0
  local out
  # On an API error (e.g. 422 for a commit never pushed) gh prints the error JSON to STDOUT and exits
  # non-zero — so trust the output only on success, and keep only the leading PR number.
  out="$(gh api "repos/{owner}/{repo}/commits/$1/pulls" \
    --jq ".[] | select(.merged_at != null and .base.ref == \"$base_name\") | .number" 2>/dev/null)" || return 0
  echo "${out%%[!0-9]*}"
}

mtime() { stat -f %m "$1" 2>/dev/null || stat -c %Y "$1" 2>/dev/null || echo 0; }

# done_reason <branch> <tip> [<worktree>]  → prints why the work is finished, or nothing.
done_reason() {
  local b="$1" tip="$2" wt="${3:-}" ahead tree pr
  # Rule 1: the branch's own PR merged at exactly this commit.
  if [[ -n "$b" ]] && merged_heads "$b" | grep -qx "$tip"; then echo "PR merged at ${tip:0:7}"; return; fi
  ahead="$(git rev-list --count "$base..$tip" 2>/dev/null || echo 0)"
  if (( ahead > 0 )); then
    # Rule 2: merging the tip into develop is a no-op (merge-tree exits non-zero on a conflict).
    # Only the first line (the merged tree's OID) matters; strip anything after it.
    if tree="$(git merge-tree --write-tree "$base" "$tip" 2>/dev/null)" && [[ "${tree%%[!0-9a-f]*}" == "$base_tree" ]]; then
      echo "its $ahead commit(s) are already in $base_name (squash-merged)"; return
    fi
    # Rule 3: GitHub says the tip commit is part of a PR merged into develop.
    pr="$(merged_pr_of_commit "$tip")"
    if [[ -n "$pr" ]]; then echo "commit ${tip:0:7} is in merged PR #$pr"; fi
    return 0
  fi
  # Rule 4: no commits of its own — finished only once idle (worktrees) / always (unused agent branches).
  if git merge-base --is-ancestor "$tip" "$base" 2>/dev/null; then
    if [[ -z "$wt" ]]; then
      if [[ "$b" == worktree-agent-* ]]; then echo "already in $base_name"; fi
      return 0
    fi
    local gitdir newest=0 t f
    gitdir="$(git -C "$wt" rev-parse --absolute-git-dir)"
    for f in "$gitdir/index" "$gitdir/HEAD" "$gitdir/logs/HEAD" "$wt"; do
      t="$(mtime "$f")"; (( t > newest )) && newest=$t
    done
    if (( $(date +%s) - newest > STALE_HOURS * 3600 )); then echo "no new commits, idle > ${STALE_HOURS}h"; fi
  fi
  return 0
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
[[ "$DRY_RUN" == "1" ]] || git worktree prune

# 2. Local branches no worktree uses.
in_use="$(git worktree list --porcelain | sed -n 's#^branch refs/heads/##p')"
for b in $(git for-each-ref --format='%(refname:short)' refs/heads/); do
  [[ " $protected " == *" $b "* ]] && continue
  grep -qx "$b" <<<"$in_use" && continue
  tip="$(git rev-parse "refs/heads/$b")"
  reason="$(done_reason "$b" "$tip")"
  [[ -z "$reason" ]] && continue
  if [[ "$DRY_RUN" == "1" ]]; then act "would delete branch $b: $reason"; continue; fi
  git branch -D "$b" >/dev/null
  act "deleted branch $b: $reason"; removed=$((removed+1))
done

say "clean-agent-worktrees: $removed removed, $kept worktree(s) kept"
