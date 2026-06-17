# Feature: Containerized launch + repeatable fresh/reuse deployment (compose → Helm/k8s)

The following plan is complete, but validate codebase patterns and task sanity before implementing. Pay special attention to existing config keys (`ConnectionStrings:Database`, `ConnectionStrings:RabbitMq`, `ReverseProxy:Clusters:<id>:Destinations:d1:Address`, `InternalAuth:PrivateKey/PublicKey`, `Gateway:BaseUrl`, `GATEWAY_URL`) and the `DevSecretGuard` (BL-11) which **refuses to boot outside `Development` with the committed dev ES256 key**.

## Feature Description

Make 3commerce launchable as a **full containerized stack** that can be (re)deployed on demand — either a clean **fresh** deployment (empty databases, init SQL re-runs, migrations re-applied) or a **reuse** relaunch (data persists) — across two environments: **dev** (committed dev keys, admin auto-seeded) and **prod-like** (rotated secrets, the BL-11 gate enforced). The same artifacts graduate to a **Helm/k8s** layer validated against a `kind` cluster in CI. The existing **bare `dotnet run`** inner-loop dev path (ADR-0009) and all current tests are preserved. Finally, **full-re-audit** the `project-analysis.html` assessment against current code and refresh the deployment docs.

## User Story

```
As an operator/developer of 3commerce
I want to launch the whole stack in containers as either a brand-new deployment or a relaunch, in dev or prod-like mode
So that I can test deployments repeatably and realistically (including the secret gate and k8s) without losing my fast bare-run dev loop
```

## Problem Statement

There is **no containerized launch today**: only `docker-compose.infra.yml` (Postgres + RabbitMQ) exists; services run bare via `dotnet run` (ADR-0009). The 10 Dockerfiles build in CI but are never run together. There is no migration path for containers (services don't self-migrate), all config is hardcoded `localhost`, there is no fresh-vs-reuse toggle, and no k8s. Separately, `docs/help/project-analysis.html` is materially stale after BL-1..BL-11.

## Solution Statement

Add a hardened `docker-compose.yml` running all 10 app images + infra on an internal network, fed by a **hybrid config** (committed `appsettings.Container.json` for host wiring + compose `env`/`env_file` for secrets/env-mode). Apply schema with **EF migration bundles** run by a one-shot migrator before services. Wrap it in **`scripts/launch.sh [--fresh|--reuse] [--env dev|prod]`**. Translate the same topology into a **Helm umbrella chart** (`deploy/helm/3commerce`) with dev/prod values, native K8s Secrets, and migrations as a pre-install/pre-upgrade **hook Job**. Validate in CI with a **compose-smoke** job and a **kind-deploy** job (`helm install` → probe). Re-audit and regenerate `project-analysis.html`, refresh deployment docs, and amend ADR-0009 for the dual launch model.

## Feature Metadata

**Feature Type**: New Capability (deployment/infra) + Refactor (config) + Docs
**Estimated Complexity**: High
**Primary Systems Affected**: All 6 services, Gateway, Notifications worker, Admin, Storefront, build (Dockerfiles), CI, docs
**Dependencies**: Docker + `docker compose`, `dotnet-ef` 10.0.9 (build-time, for bundles), Helm 3, `kind` + `kubeconform` (CI), existing `scripts/rotate-secrets.sh`

---

## CONTEXT REFERENCES

### Relevant Codebase Files — READ BEFORE IMPLEMENTING

- `docker-compose.infra.yml` — current infra-only compose (Postgres 17 + RabbitMQ 4, healthchecks, `pgdata` volume, mounts `infra/postgres/init-databases.sql`). The new full-stack compose extends this shape.
- `infra/postgres/init-databases.sql` — creates 6 DBs + roles (`<svc>_svc`/`<svc>_dev`) + extensions (`pg_trgm`, `citext`). Runs once on first volume creation. **Fresh launch = drop the volume so this re-runs.**
- `scripts/run-all.sh` — bare-run orchestration (APPS array: gateway, identity, catalog, ordering, payments, fulfillment, support, notifications). **Keep working unchanged.**
- `scripts/e2e-verify.sh` (lines 140–163 `run_live`, 288–290 teardown) — current `--live` boot: infra up → `dotnet ef database update` per service → `run-all.sh start` → storefront/admin; teardown stops processes only. Mirror its health/journey checks for `compose-smoke`.
- `scripts/rotate-secrets.sh` — emits `InternalAuth__PrivateKey`, `InternalAuth__PublicKey`, `Identity__SeedAdmin__Password` as env. **`--env prod` consumes this into an `env_file`.**
- `src/Services/Catalog/Api/Program.cs` (representative) — config read: `builder.Configuration.GetConnectionString("Database")` (jsonb via `NpgsqlDataSourceBuilder`), `AddServiceBus<CatalogDbContext>(builder.Configuration)` (reads `ConnectionStrings:RabbitMq`), `AddInternalClaimsAuth(builder.Configuration, builder.Environment)`. **Insert the `appsettings.Container.json` load right after `CreateBuilder`.**
- `src/Services/*/Api/Program.cs` — all 6 services follow the Catalog shape (each owns one DbContext + DB).
- `src/Services/*/Api/appsettings.json` — `ConnectionStrings:Database` (`Host=localhost;Port=5432;Database=<svc>_db;Username=<svc>_svc;Password=<svc>_dev`) + `ConnectionStrings:RabbitMq` (`amqp://guest:guest@localhost:5672`). Identity also holds `InternalAuth:PublicKey` + `Identity:SeedAdmin`.
- `src/Gateway/appsettings.json` (lines 11–125) — YARP `Routes` + `Clusters` (6), each cluster `Destinations:d1:Address = http://localhost:510X/`; `InternalAuth:PrivateKey` at 127. **Gateway `appsettings.Container.json` overrides the 6 `d1` addresses to `http://<svc>:8080/`.**
- `src/Gateway/Program.cs` — `InternalClaimsMinter` (BL-11 inline guard, needs `IHostEnvironment`).
- `src/BuildingBlocks/Infrastructure/Auth/DevSecretGuard.cs` + `InternalClaimsAuth.cs` (`AddInternalClaimsAuth(config, env)`) — **the gate: non-`Development` env + committed dev key ⇒ refuse to boot.**
- `src/Services/Identity/Api/DevAdminSeeder.cs` — seeds admin **only in `Development`** from `Identity:SeedAdmin`.
- `src/Workers/Notifications/Program.cs` — `AddServiceBus(...)` only, **NO DbContext** ⇒ no migration bundle.
- `src/Admin/Program.cs` (line 21) — `Configuration["Gateway:BaseUrl"]` (default `http://localhost:8080`). `src/Admin/appsettings.json` `Gateway:BaseUrl`.
- `src/Admin/Services/GatewayClient.cs`, `src/Admin/Components/Pages/Imports.razor` — admin posts `POST /api/catalog/admin/import-runs` to seed catalog (the post-fresh-launch seeding step; **no auto-seed**).
- `src/Storefront/.env.example` — `GATEWAY_URL=http://localhost:8080`; `src/Storefront/Dockerfile` — Next standalone, reads `GATEWAY_URL` at runtime; `next.config.ts` `output:"standalone"`.
- `src/Services/Catalog/Api/Dockerfile` (representative) — `sdk:10.0 → publish → aspnet:10.0 runtime`, build context = repo root. All 8 .NET Dockerfiles share this shape; `.dockerignore` excludes `bin/obj/node_modules/.git/docs/.ai-shared/.claude`.
- `.github/workflows/ci.yml` — jobs `changes` (paths-filter), `build-test`, `integration`, `browser-e2e` (gated on `changes.outputs.code`), `docker` (matrix of all 10 Dockerfiles). **Add `deploy` paths to the filter + 2 new jobs.**
- `Directory.Packages.props` — `EfCoreVersion = 10.0.9`; `Directory.Build.props` — `TargetFramework net10.0`.
- `docs/help/getting-started.md` / `deployment.md` + their `.html` (Atelier wiki) + `docs/help/assets/wiki.css|js` — docs to refresh; `docs/help/project-analysis.html` — to re-audit/regenerate.
- `docs/adr/` (ADR-0008 DB isolation, ADR-0009 bare-run local infra, ADR-0012 internal claims, ADR-0019 gateway-only frontends) — add an amendment ADR for the dual launch model.

### New Files to Create

- `src/Gateway/appsettings.Container.json` — YARP `d1` destinations → `http://<svc>:8080/`.
- `src/Services/<Svc>/Api/appsettings.Container.json` (×6) — `ConnectionStrings:Database` → `Host=postgres;…`, `ConnectionStrings:RabbitMq` → `amqp://guest:guest@rabbitmq:5672`.
- `src/Admin/appsettings.Container.json` — `Gateway:BaseUrl` → `http://gateway:8080`.
- `src/BuildingBlocks/Infrastructure/Configuration/ContainerConfig.cs` — `AddContainerConfig(this IHostApplicationBuilder)` extension: loads `appsettings.Container.json` when env var `USE_CONTAINER_CONFIG=true` (decoupled from `ASPNETCORE_ENVIRONMENT`).
- `deploy/migrator/Dockerfile` — SDK stage builds 6 self-contained EF bundles (`linux-x64`); thin runtime image + `entrypoint.sh`.
- `deploy/migrator/entrypoint.sh` — runs each `efbundle-<svc>` against `ConnectionStrings__Database__<svc>` env.
- `docker-compose.yml` — full stack (10 app images + postgres + rabbitmq), healthchecks, `depends_on` conditions, resource limits, `env_file`.
- `deploy/.env.dev` (committed) / `deploy/.env.prod` (gitignored; minted by `launch.sh --env prod`).
- `scripts/launch.sh` — `[--fresh|--reuse] [--env dev|prod]` wrapper.
- `deploy/helm/3commerce/` — `Chart.yaml`, `values.yaml`, `values-dev.yaml`, `values-prod.yaml`, `templates/` (per-app `Deployment`+`Service`, gateway+`Ingress`, storefront, admin, `ConfigMap`, `Secret`, migrate hook `Job`, `_helpers.tpl`).
- `.github/workflows/` additions: `compose-smoke` + `kind-deploy` jobs (in `ci.yml` or a new `deploy.yml`).
- `docs/adr/ADR-0021-containerized-launch.md` — dual launch model (bare-run dev + containerized deploy) amending ADR-0009.

### Relevant Documentation (read before implementing)

- EF Core migration bundles — https://learn.microsoft.com/ef/core/managing-schemas/migrations/applying#bundles — `dotnet ef migrations bundle --self-contained -r linux-x64`; bundle run with `--connection`.
- `dotnet ef` reference — https://learn.microsoft.com/ef/core/cli/dotnet — `-p/--project` (Infrastructure) and `-s/--startup-project` (Api).
- ASP.NET Core config providers / env var nesting (`__`) — https://learn.microsoft.com/aspnet/core/fundamentals/configuration/#non-prefixed-environment-variables — confirms `ConnectionStrings__Database`, `ReverseProxy__Clusters__identity__Destinations__d1__Address`.
- .NET container images default port — https://learn.microsoft.com/dotnet/core/docker/container-rid — aspnet 10 defaults to `:8080` (`ASPNETCORE_HTTP_PORTS`/`ASPNETCORE_URLS`).
- Compose `depends_on` conditions + healthcheck — https://docs.docker.com/compose/compose-file/05-services/#depends_on (`service_healthy`, `service_completed_successfully`).
- Helm hooks (`pre-install`,`pre-upgrade`, weights, delete policy) — https://helm.sh/docs/topics/charts_hooks/.
- kind + GitHub Actions + `kind load docker-image` — https://kind.sigs.k8s.io/docs/user/quick-start/ and https://github.com/helm/kind-action.
- kubeconform — https://github.com/yannh/kubeconform.

### Patterns to Follow

**Config read (services):** `builder.Configuration.GetConnectionString("Database")` and `"RabbitMq"`; never hardcode — override via `appsettings.Container.json` / env.

**Env gate (BL-11):** `--env dev` ⇒ `ASPNETCORE_ENVIRONMENT=Development` (committed keys OK, admin seeded). `--env prod` ⇒ `ASPNETCORE_ENVIRONMENT=Production` + rotated `InternalAuth__PrivateKey/PublicKey` injected, or the gateway/services **will not start** (by design — that's the test).

**Dockerfile shape:** repo-root build context; `sdk:10.0` publish → `aspnet:10.0` runtime; mirror `src/Services/Catalog/Api/Dockerfile`.

**Container config load (decoupled from env name):**
```csharp
// ContainerConfig.cs — call right after WebApplication.CreateBuilder(args)
public static TBuilder AddContainerConfig<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder {
    if (string.Equals(Environment.GetEnvironmentVariable("USE_CONTAINER_CONFIG"), "true", StringComparison.OrdinalIgnoreCase))
        builder.Configuration.AddJsonFile("appsettings.Container.json", optional: true, reloadOnChange: false);
    return builder;
}
```

**Healthcheck source:** each service exposes `/health/ready` (internal). aspnet image lacks `curl`/`wget` — add `curl` in the runtime stage (one `apt-get`), then `healthcheck: curl -fsS http://localhost:8080/health/ready`.

**No auto-seed:** after a fresh launch, seed via `POST /api/catalog/admin/import-runs` (admin Imports) — document, don't automate.

---

## IMPLEMENTATION PLAN

### Phase 1 — Container config foundation
Make every app overridable for container hostnames without touching the env-name/secret-guard semantics, and standardize the internal port.

### Phase 2 — Migration bundles + migrator image
Produce self-contained EF bundles for the 6 DB-owning services and a one-shot migrator that runs before services.

### Phase 3 — Hardened docker-compose + launch.sh
Full-stack compose (10 images + infra), healthchecks/depends_on/limits/env_file, and the fresh/reuse × dev/prod wrapper.

### Phase 4 — Helm umbrella chart
Translate the compose topology to Helm with dev/prod values, native Secrets, and a migrate hook Job.

### Phase 5 — CI: compose-smoke + kind-deploy
Boot the fresh dev compose and assert a journey; deploy the chart into kind and probe. Extend the paths-filter.

### Phase 6 — Full re-audit + docs refresh
Re-walk PRD↔code, regenerate `project-analysis.html` (Atelier), refresh deployment/getting-started docs, add ADR-0021.

### Phase 7 — Testing & validation
End-to-end: fresh/reuse × dev/prod locally; `helm lint`/`kubeconform`; kind smoke; regression `e2e-verify.sh`.

---

## STEP-BY-STEP TASKS

> Execute top-to-bottom. Branch: `feat/containerized-launch` off `develop`.

### 1. CREATE `src/BuildingBlocks/Infrastructure/Configuration/ContainerConfig.cs`
- **IMPLEMENT**: `AddContainerConfig` extension (snippet above) — loads `appsettings.Container.json` only when `USE_CONTAINER_CONFIG=true`.
- **PATTERN**: mirror namespace style of `src/BuildingBlocks/Infrastructure/Observability/OtelExtensions.cs`.
- **GOTCHA**: must run **before** `AddInternalClaimsAuth`/`GetConnectionString` so overrides are present; keep env-name orthogonal to the secret guard.
- **VALIDATE**: `dotnet build src/BuildingBlocks/Infrastructure/3commerce.BuildingBlocks.Infrastructure.csproj`

### 2. UPDATE the 8 .NET app entrypoints to call `AddContainerConfig`
- **IMPLEMENT**: add `builder.AddContainerConfig();` immediately after `WebApplication.CreateBuilder(args)` in `src/Services/{Identity,Catalog,Ordering,Payments,Fulfillment,Support}/Api/Program.cs`, `src/Gateway/Program.cs`, `src/Admin/Program.cs`. (Worker has no host config to override — optional.)
- **IMPORTS**: `using ThreeCommerce.BuildingBlocks.Infrastructure.Configuration;`
- **VALIDATE**: `dotnet build 3commerce.sln`

### 3. CREATE `appsettings.Container.json` overrides (8 files)
- **IMPLEMENT**: 6 services → `ConnectionStrings:Database = Host=postgres;Port=5432;Database=<svc>_db;Username=<svc>_svc;Password=<svc>_dev` and `ConnectionStrings:RabbitMq = amqp://guest:guest@rabbitmq:5672`. Gateway → `ReverseProxy:Clusters:<id>:Destinations:d1:Address = http://<svc>:8080/` for all 6 (`identity,catalog,ordering,payments,fulfillment,support`). Admin → `Gateway:BaseUrl = http://gateway:8080`.
- **GOTCHA**: service names `postgres`,`rabbitmq`,`identity`,… must match **both** compose service names and k8s `Service` names (name them identically so one file serves both).
- **IMPORTS**: add `<Content Include="appsettings.Container.json" CopyToOutputDirectory="PreserveNewest" />` only if the csproj doesn't already glob `appsettings*.json` (most do — verify).
- **VALIDATE**: `dotnet build 3commerce.sln && find . -name appsettings.Container.json -not -path '*/bin/*' | wc -l` → 8

### 4. ADD `curl` to the 8 .NET runtime images + confirm `:8080`
- **IMPLEMENT**: in each `aspnet:10.0` runtime stage add `RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*`; rely on the image's default `:8080` (set `ENV ASPNETCORE_URLS=http://+:8080` explicitly for clarity). Storefront image already listens on `3000`.
- **GOTCHA**: don't break the repo-root build context; keep `ENTRYPOINT` unchanged.
- **VALIDATE**: `docker build -f src/Services/Catalog/Api/Dockerfile -t t/catalog . && docker run --rm t/catalog --version >/dev/null` (build succeeds)

### 5. CREATE `deploy/migrator/Dockerfile` + `entrypoint.sh`
- **IMPLEMENT**: SDK stage: `dotnet tool install -g dotnet-ef --version 10.0.9`; for each of the 6 services run `dotnet ef migrations bundle -p src/Services/<Svc>/Infrastructure -s src/Services/<Svc>/Api --self-contained -r linux-x64 -o /bundles/efbundle-<svc>`. Runtime: small base (`mcr.microsoft.com/dotnet/runtime-deps:10.0`) + copy `/bundles` + `entrypoint.sh`.
- **`entrypoint.sh`**: for each `<svc>` run `/bundles/efbundle-<svc> --connection "$ConnectionStrings__Database__<svc>"`; exit non-zero on first failure.
- **PATTERN**: bundle invocation per EF docs (`--connection`). Connection strings injected via env (compose/Helm).
- **GOTCHA**: bundles are **self-contained** so the runtime image needs no SDK; `runtime-deps` is enough. The 6 DBs/roles must already exist (init SQL) — migrator runs **after** postgres healthy.
- **VALIDATE**: `docker build -f deploy/migrator/Dockerfile -t t/migrator .`

### 6. CREATE `docker-compose.yml` (hardened full stack)
- **IMPLEMENT**: services `postgres` + `rabbitmq` (copy from `docker-compose.infra.yml`, keep healthchecks + `pgdata` + init mount); `migrate` (migrator image, `depends_on: postgres: service_healthy`, restart `no`); 6 services + `gateway` + `notifications` + `admin` + `storefront`, each `build:` (Dockerfile) + `env_file: deploy/.env.${ENV:-dev}` + `environment: USE_CONTAINER_CONFIG=true`, `ASPNETCORE_ENVIRONMENT`, healthcheck (`curl /health/ready`), `depends_on: {postgres: service_healthy, rabbitmq: service_healthy, migrate: service_completed_successfully}`, `deploy.resources.limits` (e.g. 512M each). Gateway publishes `8080:8080`; storefront `3000:8080`→ actually `3000:3000`; admin `5200:8080`.
- **GOTCHA**: only the **gateway** (and optionally storefront/admin) need published ports; the 6 services stay on the internal network. Storefront `GATEWAY_URL=http://gateway:8080` via env. Admin `Gateway:BaseUrl` via Container.json.
- **VALIDATE**: `docker compose config -q` (valid); `docker compose --profile '' config | grep -c 'service_completed_successfully'`

### 7. CREATE `deploy/.env.dev` (committed) and wire prod env_file
- **IMPLEMENT**: `.env.dev` → `ENV=dev`, `ASPNETCORE_ENVIRONMENT=Development` (committed keys; admin seeded). `.gitignore` add `deploy/.env.prod`. `.env.prod` is generated by `launch.sh` from `rotate-secrets.sh` (`ASPNETCORE_ENVIRONMENT=Production` + `InternalAuth__PrivateKey/PublicKey` + `Identity__SeedAdmin__Password`).
- **GOTCHA**: prod DB/RabbitMQ creds remain the dev role passwords from init SQL (local kind/compose scope) — note as a documented limitation; real cluster credential rotation is deferred.
- **VALIDATE**: `grep -q 'deploy/.env.prod' .gitignore`

### 8. CREATE `scripts/launch.sh`
- **IMPLEMENT**: parse `--fresh|--reuse` (default `reuse`) and `--env dev|prod` (default `dev`). `prod` ⇒ run `scripts/rotate-secrets.sh prod` → write `deploy/.env.prod` (env-var lines). `fresh` ⇒ `docker compose down -v` first. Then `ENV=<env> docker compose --env-file deploy/.env.<env> up -d --build`. Print gateway URL + the post-launch seed hint (`POST /api/catalog/admin/import-runs`).
- **PATTERN**: bash style of `scripts/run-all.sh` / `scripts/e2e-verify.sh` (`set -euo pipefail`, arg `case`).
- **GOTCHA**: `down -v` drops `pgdata` → init SQL re-runs on next up (the "new deployment"); reuse keeps it.
- **VALIDATE**: `bash -n scripts/launch.sh && chmod +x scripts/launch.sh`

### 9. CREATE Helm umbrella chart `deploy/helm/3commerce/`
- **IMPLEMENT**: `Chart.yaml` (v2); `values.yaml` (image repo/tag, common env, `services:` list with port 8080); `values-dev.yaml` (`env: Development`, committed keys inline in a `Secret`), `values-prod.yaml` (`env: Production`, secret `name` referencing an externally-created Secret). `templates/`: `_helpers.tpl`; a ranged `deployment.yaml`+`service.yaml` for the 6 services; `gateway-*` + `ingress.yaml`; `storefront-*`, `admin-*`; `configmap.yaml` (the Container.json values, or set `USE_CONTAINER_CONFIG=true` + env); `secret.yaml` (dev only); `migrate-job.yaml` annotated `helm.sh/hook: pre-install,pre-upgrade`, `hook-weight: "-5"`, `hook-delete-policy: before-hook-creation,hook-succeeded` running the migrator image.
- **GOTCHA**: k8s `Service` names must equal the compose names so `appsettings.Container.json` resolves identically. Probes: `readinessProbe httpGet /health/ready :8080`.
- **VALIDATE**: `helm lint deploy/helm/3commerce -f deploy/helm/3commerce/values-dev.yaml`; `helm template deploy/helm/3commerce -f .../values-dev.yaml | kubeconform -strict -summary`

### 10. CREATE secret provisioning for prod Helm
- **IMPLEMENT**: a short `deploy/helm/make-secret.sh` that runs `scripts/rotate-secrets.sh prod` and `kubectl create secret generic 3commerce-secrets --from-literal=...` (ES256 keys, admin password); `values-prod.yaml` references it by name.
- **VALIDATE**: `bash -n deploy/helm/make-secret.sh`

### 11. UPDATE `.github/workflows/ci.yml` — paths-filter + 2 jobs
- **IMPLEMENT**: add `deploy/**`, `docker-compose.yml`, `Dockerfile` paths to the `changes.code` positive list. Add job **`compose-smoke`** (`needs: changes`, `if: code == 'true'`): `docker compose up -d --build` (dev), wait gateway `/health` (or `register` 202), one journey, `docker compose down -v`. Add job **`kind-deploy`**: `helm/kind-action` create cluster → build the 10 images + migrator → `kind load docker-image` → `helm install 3commerce deploy/helm/3commerce -f values-dev.yaml --wait --timeout 12m` → port-forward gateway → probe → teardown. Both paths-gated; keep existing jobs.
- **GOTCHA**: `helm --wait` + good readiness probes reduce flake; `kind load` each image (no registry). Honor the user-accepted slowness.
- **VALIDATE**: `python3 -c "import yaml;yaml.safe_load(open('.github/workflows/ci.yml'))"`; push branch → both jobs green.

### 12. CREATE `docs/adr/ADR-0021-containerized-launch.md`
- **IMPLEMENT**: record the **dual launch model** — bare `dotnet run` (ADR-0009) for inner-loop dev **and** containerized compose/Helm for deployment testing; the fresh/reuse × dev/prod matrix; migration-bundle choice; hybrid config; the BL-11 prod-gate interaction.
- **VALIDATE**: file exists, links ADR-0009.

### 13. Full re-audit → regenerate `docs/help/project-analysis.html`
- **IMPLEMENT**: run a PRD↔code conformance re-audit (agent-driven, mirroring the original 3-agent review) across all FRs/NFRs against current code; produce a fresh scorecard (expected ≈ 20 Met / ~1 Partial / 0 Missing, grade ≈ A) and regenerate `project-analysis.html` in the **Atelier** system (reuse `docs/help/assets/wiki.css|js`, `.callout good/warn/bad`, `.pill`, `.statline`, `.verdict`; keep sidebar/topbar/prev-next).
- **GOTCHA**: remove now-false "weakest parts" (FR-7 missing, placeholder screens, untested NFR-2/5/7, free-form RMA, missing FR-12, app Dockerfiles, secret gate). Keep still-true launch gates.
- **VALIDATE**: `node --check docs/help/assets/wiki.js`; `python3` well-formedness check on the page; `grep -c 'href="[^"]*\.md"' docs/help/project-analysis.html` → 0.

### 14. Refresh deployment docs
- **IMPLEMENT**: update `docs/help/deployment.md` + regenerate `deployment.html` (compose/Helm/`launch.sh`, fresh/reuse × dev/prod, kind in CI, the seed step, deferred gates). Add a "Containerized launch" section to `getting-started.md`/`.html` while **keeping** the bare-run section. Refresh `AGENTS.md` known-gaps line (BL-1..BL-11 done; containerized launch added).
- **VALIDATE**: wiki well-formedness checks; links resolve.

### 15. UPDATE status + e2e-verify test-list
- **IMPLEMENT**: mark plan tasks in `.ai-shared/plans/plan_status_executions.md`; if a `compose-smoke`/kind path is added to `e2e-verify.sh` (optional `--container`), update its header list per the test-list rule.
- **VALIDATE**: `scripts/e2e-verify.sh` (automated tiers still green).

---

## TESTING STRATEGY

### Unit Tests
- `ContainerConfig` is config plumbing; covered indirectly by build + smoke. Optional: a small test asserting `AddContainerConfig` loads the file only when the env var is set (xUnit, in any service test project that references BuildingBlocks.Infrastructure).

### Integration Tests
- Existing 35 Testcontainers integration tests must stay green (no behavior change to services).
- New **deployment** validation is end-to-end, not xUnit: `compose-smoke` (CI) + `kind-deploy` (CI) + local `launch.sh` matrix.

### Edge Cases
- **Fresh vs reuse**: `--fresh` drops `pgdata` (init SQL re-runs, migrator re-applies, catalog empty until imported); `--reuse` preserves data and the migrator no-ops.
- **dev vs prod gate**: `--env prod` **without** rotated keys ⇒ gateway/services refuse to boot (assert this is the BL-11 gate, not a bug); **with** rotated keys ⇒ boots.
- **Migrator ordering**: services must not start before `migrate` completes (compose `service_completed_successfully` / Helm hook).
- **Healthcheck**: aspnet image without `curl` would mark services unhealthy → never ready (Task 4).
- **Worker**: Notifications has no DB — must NOT be in the migrator/bundles.
- **Name parity**: compose service names == k8s Service names == `appsettings.Container.json` hosts.

---

## VALIDATION COMMANDS

### Level 1 — Syntax & Style
```bash
dotnet build 3commerce.sln                       # 0 warnings
dotnet format 3commerce.sln --verify-no-changes --no-restore
bash -n scripts/launch.sh deploy/migrator/entrypoint.sh
docker compose config -q
helm lint deploy/helm/3commerce -f deploy/helm/3commerce/values-dev.yaml
python3 -c "import yaml;yaml.safe_load(open('.github/workflows/ci.yml'))"
```

### Level 2 — Unit/Contract
```bash
dotnet test 3commerce.sln --no-build --filter 'Category!=Integration'
```

### Level 3 — Integration (Testcontainers)
```bash
dotnet test tests/3commerce.IntegrationTests --filter Category=Integration   # 35 green
```

### Level 4 — Manual / deployment
```bash
# images
for d in $(find . -name Dockerfile -not -path '*/bin/*' -not -path '*/node_modules/*'); do docker build -f "$d" -q . ; done
docker build -f deploy/migrator/Dockerfile -q .

# fresh dev launch + journey
scripts/launch.sh --fresh --env dev
curl -fsS localhost:8080/api/identity/register -X POST -H 'content-type: application/json' -d '{"email":"a@b.com","password":"passwordpass"}' -o /dev/null -w '%{http_code}\n'   # 202
# seed catalog (no auto-seed): admin Imports OR
# (admin UI) http://localhost:5200 -> Imports -> Run importer

# reuse relaunch (data persists)
scripts/launch.sh --reuse --env dev

# prod gate: without secrets must fail; launch.sh --env prod mints+injects then boots
scripts/launch.sh --fresh --env prod
docker compose down -v

# k8s static + kind
helm template deploy/helm/3commerce -f deploy/helm/3commerce/values-dev.yaml | kubeconform -strict -summary
```

### Level 5 — Regression
```bash
scripts/e2e-verify.sh            # automated tiers
scripts/e2e-verify.sh --live     # bare-run path still works (ADR-0009 preserved)
```

---

## ACCEPTANCE CRITERIA

- [ ] `scripts/launch.sh --fresh --env dev` brings the full containerized stack up; gateway health + a journey pass; catalog seedable via admin import.
- [ ] `--reuse` relaunch preserves data; migrator no-ops.
- [ ] `--env prod` enforces the BL-11 gate (fails without rotated keys, boots with them).
- [ ] EF bundles apply all 6 schemas on a fresh empty DB; Notifications worker excluded.
- [ ] `helm lint` + `kubeconform` pass; `kind-deploy` CI job deploys and probes green.
- [ ] `compose-smoke` CI job green; paths-filter runs deploy jobs only on relevant changes.
- [ ] Bare-run dev (`run-all.sh`, `e2e-verify.sh --live`) and all 35 integration tests still pass — no regressions.
- [ ] `project-analysis.html` re-audited, rescored, Atelier-styled, no `.md` links, no false gaps.
- [ ] Deployment + getting-started docs refreshed; ADR-0021 added; AGENTS.md gaps line current.

## COMPLETION CHECKLIST

- [ ] All tasks done in order; each validation passed.
- [ ] Full build + format clean; unit + 35 integration green.
- [ ] Compose matrix (fresh/reuse × dev/prod) verified locally.
- [ ] Helm lint/kubeconform + kind deploy green.
- [ ] CI: existing + `compose-smoke` + `kind-deploy` green; docs-only PRs still skip browser-e2e.
- [ ] Docs/ADR/analysis updated and validated.

## NOTES

- **Decisions (grilled):** full containerized launch (bare-run kept for dev); env toggle dev/prod; EF migration bundles; hybrid config (`appsettings.Container.json` + env/`env_file`); no fresh-launch auto-seed; full re-audit of `project-analysis.html`; + k8s (Helm umbrella) track; CI compose-smoke **and** kind cluster.
- **Decoupling:** `appsettings.Container.json` is loaded by `USE_CONTAINER_CONFIG=true`, **not** by `ASPNETCORE_ENVIRONMENT`, so dev/prod (secret guard + admin seeder) stays orthogonal to host wiring. One `Container.json` serves both compose and k8s via identical service names.
- **Honest limitations (documented, deferred):** prod mode rotates app secrets (ES256 + admin) but local DB/RabbitMQ creds remain the init-SQL dev passwords; real cluster credential rotation + a secrets store (sealed-secrets/external) are out of scope. Live Stripe/Xero, real `ITaxStrategy`, external pen test remain launch gates.
- **ADR-0009 tension:** intentionally adds a containerized path **without** removing bare-run; captured in ADR-0021.
- **CI cost:** kind is slow/flaky (accepted) — paths-gate it and use `helm --wait` + tight readiness probes.

**Confidence (one-pass): 7.5/10** — config/compose/launch.sh/bundles are well-scoped and pattern-backed; the kind-deploy CI job and the full re-audit carry the most uncertainty (cluster flake; audit is judgment-heavy).
