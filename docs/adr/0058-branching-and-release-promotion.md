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

`browser-e2e` runs on two infra sets (`INFRA_SET`, `scripts/lib/infra.sh`). PRs and `develop` pushes use
the fast **`core`** set (postgres, rabbitmq, valkey). Pushes to **`test` and `main`** use the **`full`**
set (13 containers: adds Kafka, Kafka UI, pgAdmin, redis-exporter and the LGTM observability stack), so the
infra-portal and observability Playwright specs run there instead of skipping. On `core` they skip.
`promote.sh` reads the latest check run per gate name. After `develop → test`, the latest `browser-e2e` on
the release candidate is therefore the full-set run, and `test → main` is gated on it.

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
- The release stages run `browser-e2e` on the **full infra set**. The portal and observability specs
  (Kafka UI, pgAdmin, Grafana, Loki/Tempo/Mimir, and logs and traces from a real checkout) block
  `test → main`. On a PR they skip. The job takes about 1–3 minutes longer on that set. Trial (PR #278: two
  runs of one commit on the public-repo runner, 4 vCPU / 16 GB):
  - both runs green: 113 passed, 0 flaky, 0 skipped;
  - all 7 portal specs ran;
  - the job took 13m48s and 12m17s, against 10m23s–11m40s on `core`, with a 40-minute timeout;
  - peak load came during service boot, before Playwright; available memory never fell below 6.2 GB.

  A portal or observability regression introduced on a PR therefore surfaces at the `test` promotion, not
  at merge. Run `CI=true INFRA_SET=full scripts/e2e-verify.sh --live-only` locally when a change touches
  that infra.
- A `develop` tip with a red gate cannot be promoted. Fix forward on `develop` with another PR — never
  force-push a stage.
- **Hotfixes** follow the same path (fix branch → `develop` → `test` → `main`); there is no side door.
  If production ever needs an emergency route, that is a new decision.
- Limitation: rulesets cannot check ancestry, so they would accept a direct push to `test`/`main` of any
  fully-green commit (for example a feature PR's head). The flow above, `promote.sh`, and the agent rules
  in `AGENTS.md` are what keep promotions to the `develop → test → main` order.
