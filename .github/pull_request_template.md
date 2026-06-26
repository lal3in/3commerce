## Summary

<!-- What changed and why. Link the plan/task. -->

## Canonical-rules checklist (see AGENTS.md → Rules)

- [ ] **Plan status tracker** — `.ai-shared/plans/plan_status_executions.md` updated (row status `pending`→`in_progress`→`done` + execution detail in **Comments**). _Not_ status-only-in-todos; _no_ separate `*-followups.md`/notes doc — canonical files only.
- [ ] **Build / format / tests** green (`dotnet build 3commerce.sln`, `dotnet format --verify-no-changes`, relevant unit/integration; `scripts/e2e-verify.sh` if it's a regression-worthy change).
- [ ] **ADR** added/updated for any architectural decision (+ `docs/adr/adr_index.md`).
- [ ] **API contracts** — `docs/api/api_contracts_index.md` updated if endpoints changed.
- [ ] **Regression list** — `scripts/e2e-verify.sh` updated if a test was added/removed/renamed.
- [ ] **Service list** — `scripts/lib/services.sh` (+ the non-derived lists: CI matrix/df, migrator loop, init-databases.sql) updated if a service was added/removed.
- [ ] **Maintenance triggers** — any other files in the AGENTS.md "Keep these in sync" map updated (new infra → `host-check.sh`/`doctor.sh`; UI change → screenshot specs; permissions → `roles-permissions.html`; etc.).

## Verification

<!-- Commands run + expected output (e.g. build 0/0, N tests green, screenshots). -->
