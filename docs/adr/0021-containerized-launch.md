# ADR-0021: Containerized launch (compose → Helm/k8s) alongside bare-run dev

- **Status:** Accepted
- **Date:** 2026-06-17
- **Amends:** [ADR-0009](0009-local-dev-bare-dotnet-run.md) (bare `dotnet run` for local dev)

## Context

ADR-0009 deferred Kubernetes and ran services bare for the fastest inner loop. Post-MVP we want a **repeatable, realistic deployment** we can trigger as a brand-new install or relaunch, in a dev or prod-like environment — to exercise the full stack (including the BL-11 secret gate) the way it would actually deploy. The inner dev loop still matters, so we are not replacing bare-run.

## Decision

Add a **dual launch model**:

- **Inner-loop dev (unchanged, ADR-0009):** `scripts/run-all.sh` (bare `dotnet run`) + `docker-compose.infra.yml` (Postgres + RabbitMQ only).
- **Deployment launch (new):** `docker-compose.yml` runs all 10 app images + infra on one internal network, wrapped by `scripts/launch.sh [--fresh|--reuse] [--env dev|prod]`. The same topology is packaged as a **Helm umbrella chart** (`deploy/helm/3commerce`) and validated against a `kind` cluster in CI.

Supporting choices:

- **Config:** committed `appsettings.Container.json` per app for host wiring (Postgres/RabbitMQ/service names, gateway YARP destinations), loaded by `USE_CONTAINER_CONFIG=true` — **decoupled from `ASPNETCORE_ENVIRONMENT`** so the dev/prod environment (which drives the BL-11 `DevSecretGuard` and the admin seeder) stays orthogonal. Service names are identical across compose and k8s, so one file serves both.
- **Migrations:** framework-dependent **EF migration bundles** (one per DB-owning service) built into a one-shot `migrate` image, run after Postgres init SQL and before the app services. **Design-time `DbContext` factories** let the bundles build the context without the app host (no auth/RabbitMQ/secret-guard at migrate time); `--connection` supplies the DB.
- **Fresh vs reuse:** `--fresh` = `docker compose down -v` (drop the `pgdata` volume → init SQL re-runs → migrations re-apply → empty catalog); `--reuse` keeps data.
- **dev vs prod:** `--env dev` uses the committed dev ES256 keys (admin auto-seeded). `--env prod` mints a fresh keypair, injects it via a compose overlay (real-newline PEMs ride interpolation; env-files can't hold multiline), and the BL-11 gate is enforced.
- **No fresh-launch auto-seed:** the catalog comes up empty; seed via the admin Imports screen.

## Alternatives considered

- **Migrate-on-startup** — rejected: couples migrations to app boot/secrets; debated for prod.
- **Self-contained `linux-x64` bundles** — rejected: won't run on local arm64 (colima); framework-dependent + multi-arch base works on both arm64 and amd64.
- **Pure env-var config (no `appsettings.Container.json`)** — rejected: verbose YARP nesting; structural host wiring reads better as committed JSON.
- **Replace bare-run with compose** — rejected: slower C# debug loop (the original ADR-0009 reason still holds).
- **Live Stripe/Xero, real `ITaxStrategy`, external pen test** — out of scope (business/external launch gates).

## Consequences

- Two config matrices (dev/prod × compose/k8s) to keep in sync — mitigated by the shared `appsettings.Container.json` + Helm values.
- Prod mode rotates app secrets (ES256 + admin) but local DB/RabbitMQ creds remain the init-SQL dev passwords; real cluster credential rotation + a secrets store are deferred.
- The kind-deploy CI job is slow/flaky (accepted) — paths-gated, with `helm --wait` + tight readiness probes.
- Cheap to evolve toward a registry + real cluster later; the chart and launch wrapper are the seam.
