# 0058 — Branching and release promotion: feature/defect → develop → test → main

Status: Accepted — implemented (GitHub rulesets active 2026-10-05)
Area: Repository / CI / release process
Supersedes: the implicit "every PR squash-merges straight into `main`" flow used since June 2026

## Context

Since mid-June every change went straight from a feature branch into `main`. `main` was not protected:
the "required gates" (`changes`, `build-test`, `integration`) were a convention the merge poller followed,
not something GitHub enforced, and the slower suites that exercise the whole system — `browser-e2e`
(Playwright), `compose-smoke`, `kind-deploy` — gated nothing. A change could reach `main` with those red,
and the policy that red non-required suites are still follow-ups depended on someone noticing.

`main` is also what a production deployment is cut from. There was no place where an integrated set of
changes could sit, fully tested, before becoming production — and no point at which the owner said
"release this".

An early `develop` branch (PRs #3–#9, June 2026) had been abandoned; it was 398 commits behind `main`.

## Decision

Four kinds of branch, one direction of travel:

| Branch | Holds | How changes arrive | Gates (GitHub ruleset) |
|---|---|---|---|
| `feature/*`, `fix/*`, `docs/*`, `chore/*`, `test/*` | one change | commits | — (CI runs on the PR) |
| `develop` | integrated, individually-tested changes | **squash-merged PR** from a feature/defect branch — the only way in | PR required (squash only), `changes`, `build-test`, `integration`, `browser-e2e`; no force-push, no deletion |
| `test` | the release candidate — one release away from production | **fast-forward** of `develop`'s tested tip (`scripts/promote.sh test`) | `changes`, `build-test`, `integration`, `browser-e2e`, `compose-smoke`, `kind-deploy` green on the commit; fast-forward only; linear history; no deletion |
| `main` | **production** | **fast-forward** of `test`'s tip (`scripts/promote.sh main --user-approved`), **only after the owner approves that release** | same as `test` |

1. **Feature/defect branches are cut from `develop`** (never from `test` or `main`) and each is tested on
   its own: its PR must pass the four `develop` gates — including the Playwright suite — before it merges.
2. **Promotions are fast-forwards, never merges.** `test` and `main` only ever move to a commit that already
   exists on the stage before them, so the code released is byte-for-byte the code that passed. GitHub's
   merge button cannot fast-forward, so a promotion is a push of the existing commit
   (`git push origin <sha>:refs/heads/test`), done by `scripts/promote.sh`, which refuses unless the move
   is a fast-forward and every gate is green on that commit.
3. **Check results travel with the commit.** CI check runs attach to a commit, and the `develop` push
   already ran the full suite on it; the `test`/`main` rulesets require those results on the pushed commit,
   so a promotion of an untested or red commit is rejected by GitHub itself. `test` is also a CI push
   trigger, so each promotion re-runs the suite against the release candidate.
4. **Production is never automatic.** Agents promote feature work into `develop` and `develop` into `test`
   when gates are green, then stop and ask the owner before each `test → main` release.
   `promote.sh main` refuses without `--user-approved`.
5. `develop` is the repository's default branch, so new PRs and agent worktrees start from it.

## Consequences

- `scripts/pr-merge-on-green.sh` merges only PRs into `develop`, and waits for `browser-e2e` as well. PRs
  against `test`/`main` are refused — those branches move only by promotion.
- The `.githooks/pre-push` format check skips commits the remote already has, so a promotion push costs
  nothing locally.
- The full Playwright and deploy-smoke suites now gate a release, not just a merge. A red smoke
  blocks promotion to `test` until fixed or genuinely re-run green.
- A `develop` tip with a red gate cannot be promoted. Fix forward on `develop` with another PR — never
  force-push a stage.
- **Hotfixes** follow the same path (fix branch → `develop` → `test` → `main`); there is no side door.
  If production ever needs an emergency route, that is a new decision.
- Limitation: rulesets cannot check ancestry, so they would accept a direct push to `test`/`main` of any
  fully-green commit (for example a feature PR's head). The flow above, `promote.sh`, and the agent rules
  in `AGENTS.md` are what keep promotions to the `develop → test → main` order.
