# AGENTS.md

This file provides guidance to AI Agents when working with code in this repository.

## Project Overview

**3commerce** is a from-scratch e-commerce platform for physical goods sourced from large third-party catalogs, built as C# microservices (Identity, Catalog, Entity, Ordering, Payments, Fulfillment, Support) communicating async-first over RabbitMQ via MassTransit, each owning its own PostgreSQL database. A YARP gateway is the single public origin; the storefront is Next.js (SSR), admin is Blazor Server. Money flows through a custom double-entry ledger (source of truth) with Stripe (test mode) as the v1 rail and nightly journal sync to Xero. The project is deliberately dual-purpose: a launchable real business **and** a hands-on distributed-systems learning vehicle — production quality is required, shortcuts are not. Full rationale lives in the PRD decision log (`docs/prd/3commerce/15-appendix.md`).

> **Status:** MVP on dev/test rails (Phases 1–4), **conformance grade A−→A** (16 Met / 4 Partial / 0 Missing of 21 FR/NFR — see `docs/reviews/prd-vs-implementation.md`). All six services, gateway, Next.js storefront, and Blazor admin built and validated: custom auth, catalog + search, cart + checkout saga, append-only double-entry ledger, Stripe-abstracted payments (+ fake for keyless dev), refunds, Fulfillment shipments, Support + RMA saga (single refund path), and Xero summary journals (logging client; real OAuth a future swap). Tests: **11 unit + 27 integration + 13 Playwright browser E2E** (storefront + admin), green in CI; `scripts/e2e-verify.sh --live` covers **L1–L20**.
>
> **Post-MVP work done (`.ai-shared/plans/plan_status_executions.md`):** backlog BL-1..BL-11 complete — FR-7 guest→account, FR-12 admin catalog CRUD, real admin Orders / storefront account screens, NFR-2/5/7 now asserted by tests, per-line server-derived RMA, configurable `Store:Currency`, app-tier Dockerfiles, and the BL-11 dev-secret launch gate. **Containerized launch (ADR-0021):** `scripts/launch.sh [--fresh|--reuse] [--env dev|prod]` runs the full stack via `docker-compose.yml` (EF-bundle migrator), and a Helm chart (`deploy/helm/3commerce`) is `kind`/CI-validated. **Still deferred (launch gates):** live Stripe/Xero, real `ITaxStrategy`, external pen test, a managed cloud cluster — `docs/prd/3commerce/15-appendix.md`. Frontend wiki (HTML — open `docs/help/index.html`); analysis: `docs/help/project-analysis.html`.

---

## Collaboration protocol (always follow)
- Ask clarifying questions when requirements are ambiguous.
- Prefer small, incremental changes over large rewrites.
- Always provide verification steps (tests run, commands, expected output).
- If a task references product scope/UX/requirements: consult PRD (see below) and produce a Working Brief before coding.

---

## Sources of truth (do NOT auto-load PRD)
- Product requirements: ./docs/prd/PRD.md (load only if task depends on requirements)
- Architecture decisions: ./docs/adr/
- API contracts: ./docs/api/

### PRD Loading Rule
Only read PRD sections when the task involves:
- new feature implementation,
- changes to user flows,
- acceptance criteria / scope questions,
- rollout
- telemetry/metrics requirements.

When PRD is needed:
1) Read only the relevant PRD sections.
2) Write a short "Working Brief" in the chat:
   - Goal
   - Non-goals
   - Requirements (FR-#, NFR-#)
   - Acceptance criteria
   - Test/verification plan
3) Implement to the brief and verify via commands below.

---

## Tech Stack

| Technology | Purpose |
|------------|---------|
| .NET 10 (LTS) / C# | All backend services, gateway, admin |
| ASP.NET Core minimal APIs | One small HTTP surface per service |
| EF Core 10 + Npgsql | Persistence + migrations, one DbContext per service |
| PostgreSQL 17 | One container, **one database + login per service** (ADR-0008), each service's tables in a **named schema** within its DB (`<service>.*`, ADR-0022); FTS + pg_trgm + JSONB for catalog search |
| MassTransit v8 + RabbitMQ | Async events, saga state machines (checkout, refund), EF transactional outbox |
| YARP | Gateway: single public origin, session validation, internal claims, rate limiting |
| Next.js (App Router, SSR) + TypeScript + Tailwind | Storefront (SEO at catalog scale, rich UX) |
| Blazor Server | Admin app (all-C# internal tooling) |
| Stripe.net (test mode) | v1 payment rail behind `IPaymentProvider`; Payment Element client-side (SAQ-A) |
| Xero API (OAuth2) | Nightly summary journals + per-refund postings |
| Argon2id (vetted library) | Password hashing — custom auth flows, never custom crypto |
| OpenTelemetry | Distributed traces across gateway → services → consumers |
| xUnit + Testcontainers | Tests against real Postgres/RabbitMQ; MassTransit test harness |

---

## Commands

```bash
# Development — bare-run, ADR-0009 (the light default; never builds images, so it can't OOM the Docker VM)
scripts/dev-up.sh --with-frontends --seed          # ONE command: infra + migrate + all services + frontends + seed
scripts/dev-down.sh                                 # stop everything
# ...or piecemeal:
docker compose -f docker-compose.infra.yml up -d   # Postgres 17 + RabbitMQ
dotnet run --project src/Services/<Name>/Api        # per service; same for Gateway, Workers
cd src/Storefront && npm run dev                    # Next.js storefront

# Containerized launch (full stack in containers) — ADR-0021
# Building all 13 .NET images needs a Docker VM with ~8+ GiB RAM. `docker compose up --build` builds them in
# PARALLEL and WILL OOM a small VM (default 6 GiB Colima) and crash the daemon. Use the bounded builder below,
# or bump the VM first: `colima stop && colima start --cpu 4 --memory 12`.
scripts/build-images.sh                             # builds every image PARALLEL=2 at a time, with a memory preflight
scripts/launch.sh --fresh --env dev                 # brand-new deployment (down -v + build + up)
scripts/launch.sh --reuse --env dev                 # relaunch, keep data
scripts/launch.sh --fresh --env prod                # rotated keys, BL-11 gate enforced

# Build
dotnet build 3commerce.sln
cd src/Storefront && npm run build

# Lint
dotnet format --verify-no-changes
cd src/Storefront && npm run lint

# Typecheck
# (C#: covered by build)
cd src/Storefront && npx tsc --noEmit

# Storefront E2E (Playwright/Chromium; requires the stack running + storefront on :3000)
cd src/Storefront && npm run test:e2e

# Unit Tests
dotnet test 3commerce.sln

# E2E Integration (Testcontainers spins up Postgres/RabbitMQ; Docker must be running)
dotnet test tests/ --filter Category=Integration

# Full regression check (run after building new features)
scripts/e2e-verify.sh          # automated suites (build, format, unit, integration, storefront, vuln)
scripts/e2e-verify.sh --live   # also boots the stack and runs live user-journey smoke flows
```

---

## Debugging — where things log + one-shot triage

Run the tool first; hand-tail logs only when it points you somewhere.

- `scripts/doctor.sh` — local env in one shot: infra containers, every service's `/health/ready`
  (manifest-driven), the frontends, and the last error lines from `.run/<svc>.log` for anything down.
- `scripts/host-check.sh [--deep] [--logs] [target]` — full sweep of a host (containers, health, **RabbitMQ bus state**, infra logs, observability, compose, resources, Colima OOM log). Runs over local / SSH VPS / GCP (`scripts/lib/hosts.sh`), so the same diagnosis works on Hostinger/EC2/GCE/Azure; `--logs` adds CloudWatch/GCP/Azure managed logs.
- `scripts/ci-logs.sh [branch]` — the latest CI run's **failing jobs + their error lines** (automates
  `gh run view --job <id> --log | strip-ansi | grep <error-signatures> | tail`). Defaults to the current branch.

Bare-run + compose-dev logs are **verbose by default** (app `Debug` + EF SQL + MassTransit), so a failure usually carries its own diagnosis — no need to reproduce with more logging. Quieten bare-run with `LOG_LEVEL=Information scripts/run-all.sh start`; compose verbosity is in `deploy/.env.dev`.

Log locations:

| Where | How to read it |
|---|---|
| Bare-run services | `.run/<name>.log` (e.g. `.run/payments.log`) |
| Storefront / admin | `/tmp/3c-storefront.log` · `/tmp/3c-admin.log` |
| Containerized services | `docker compose logs <service>` |
| CI job | `gh run view --job <id> --log` (or just `scripts/ci-logs.sh`) |
| kind-deploy pod events | inside the **kind-deploy job log** (the "Diagnostics on failure" step) — NOT `docker logs` |

Lesson from the field: **read the failing step's RAW log before tuning.** Cascading symptoms mislead —
kind-deploy's real cause was `no space left on device`, not the probe timeouts it looked like; a
compose-smoke failure was a NuGet **cache-mount race**, not a code bug. Match the fix to the first error.

## Project Structure

```
3commerce/
├── AGENTS.md                      # this file
├── docs/
│   ├── prd/                       # PRD index + section files (do not auto-load)
│   ├── adr/                       # architecture decision records + adr_index.md
│   ├── api/                       # API contract files + api_contracts_index.md
│   ├── reference/                 # working guidelines: components.md, api.md
│   ├── security/                  # asvs-l1-audit.md
│   └── runbooks/                  # mvp-walkthrough.md
├── docker-compose.infra.yml       # Postgres 17 + RabbitMQ 4 only (ADR-0009)
├── infra/postgres/                # init-databases.sql (6 DBs + roles + extensions)
├── scripts/run-all.sh             # start/stop gateway + services + worker locally
├── .github/workflows/ci.yml      # build, format, unit, integration, docker matrix
├── 3commerce.sln                  # all 27 projects; Directory.Build.props / Directory.Packages.props (CPM)
├── src/
│   ├── BuildingBlocks/
│   │   ├── Contracts/             # message contracts ONLY (versioned additively)
│   │   └── Infrastructure/        # AddServiceBus (outbox/inbox), AddServiceTelemetry, ProblemDetails, health
│   ├── Gateway/                   # YARP (port 8080); Dockerfile per runnable project
│   ├── Services/
│   │   ├── Identity/  ├── Catalog/  ├── Entity/  ├── Ordering/
│   │   ├── Payments/  ├── Fulfillment/  └── Support/
│   │   #  each: Api/ Domain/ Infrastructure/ + tests/; ports 5101-5107
│   ├── Workers/Notifications/     # email worker (event consumer, not a service)
│   ├── Storefront/                # Next.js storefront (+ e2e/ e2e-admin/ Playwright suites)
│   ├── Admin/                     # Blazor Server operator console (:5200)
│   ├── SupplierPortal/            # Blazor Server supplier portal (:5300)
│   └── Cli/                       # .NET global-tool CLI skeleton (3commerce.Cli)
└── tests/3commerce.IntegrationTests/  # Testcontainers spine tests (outbox, redelivery, idempotency)
```

---

## Architecture

Event-driven microservices cut along business-capability seams. State changes propagate as MassTransit events over RabbitMQ; synchronous REST between services is allowed only for read-time queries, never inside a saga step. Checkout (Ordering) and refund (Support → Payments) are MassTransit saga state machines with timeouts and compensation. Every "write DB + publish event" goes through the EF Core transactional outbox; every consumer is idempotent (dedup by message ID; Stripe webhooks by event ID).

Data: hard isolation — no cross-database joins, no shared domain types. When a service needs another's data for display (e.g. product names on orders), it keeps a local copy updated via events. Each service's tables (domain + the MassTransit outbox/inbox/saga-state) live in a **named schema** within its own database (`<service>.*`, not `public`; ADR-0022). The Payments ledger is append-only double-entry and is the source of truth for all money facts; Stripe is a rail, Xero is a downstream report.

> **Adding a DB-owning service — named-schema rules (ADR-0022):** `modelBuilder.HasDefaultSchema("<svc>")`; pin `MigrationsHistoryTable("__EFMigrationsHistory", "public")` on `UseNpgsql`; set `PostgresLockStatementProvider(enableSchemaCaching: false)` on the EF outbox **and** every saga `EntityFrameworkRepository` (the schema cache leaks across services in the in-process tests otherwise); **schema-qualify all raw SQL** (EF qualifies automatically; raw SQL does not); generate migrations with the schema baked in (`EnsureSchema`), never a `SET SCHEMA` move.

Auth: opaque session token in a Secure/HttpOnly cookie, validated at the gateway against Identity (cached ≤ 60 s), converted to a short-lived signed internal-claims JWT that services verify by signature only. Public traffic reaches services exclusively through the gateway.

Multi-tenancy (platform expansion, in progress on `feat/mt-phase1-foundation`): the platform is being made **strictly multi-tenant** — a tenant is one legal operating business; `Principal`s span tenants; customers are tenant-scoped (email unique per tenant). Tenant-owned rows carry `TenantId` and are isolated by **PostgreSQL RLS** (transaction-scoped `set_config('app.tenant_id', …, true)`, `FORCE ROW LEVEL SECURITY`, non-superuser service role) **plus** application checks. Authorization is a central **PDP in Identity** + service-side **PEP**, with fully dynamic admin-defined **RBAC** over a code-defined permission registry. New surfaces: an **Entity** master-data service, a generic **SupplierPortal**, and an installable **.NET global-tool CLI** (`src/Cli`, Gateway-only). See **ADR-0023** (strict multi-tenancy), **0024** (RLS), **0025** (PDP/PEP + RBAC), **0026** (service accounts + CLI), **0027** (Entity boundary).

> **Composable supply (ADR-0028, Phase 4 mt4_*):** how a product is *sourced/delivered/charged* lives on a first-class **Offer** (product supply profile) `(product/variant × supplier) → supply category + fulfilment type + price + pricing model`, not on the product. One shared `FulfilmentType`/`SupplyCategory`/`BillingMode`/`PricingModel` vocabulary (`BuildingBlocks.Contracts.Supply`) replaces the old per-service enums + the bus string. Fulfillment owns **inventory + reservations + an append-only movement ledger** (single stock owner; Catalog mirrors availability), **carrier integrations** (tenant default + per-storefront override; `CredentialRef` only), **shipping quotes** (`ISupplyStrategy`/`ICarrierRateProvider` seams, Fake keyless + AusPost/DHL sandbox, quote expiry/revalidation/fallback), and **dropship** supplier-order forwarding (`ISupplierOrderProvider`, Fake-first) + a supplier availability feed. Checkout resolves each line's offer from a local `OfferCopy` read model (Catalog `OfferChanged`); `OrderConfirmed` carries tenant + ship-to + per-line supply so Fulfillment consumes warehouse stock and auto-forwards dropship lines. Digital supply (entitlements/usage/subscriptions/pricing) is the Phase-7 axis behind the same seam.

> **Writing tenant-owned tables under FORCE RLS (ADR-0024):** every write/read must run inside a tenant scope — `db.RunInTenantScopeAsync(TenantContext.ForTenant(id), …)` or `BeginTenantScopeAsync` (tenant scope for tenant-keyed ops; **platform scope** for secret-keyed cross-tenant lookups like session introspection). An unscoped `INSERT` fails the `WITH CHECK` policy (`42501`) as the non-superuser service role — and superuser-connected tests will *not* catch it, so validate via the containerized launch / a non-superuser test (see `IdentityUsersRlsTests`). Secret-keyed tables (Identity `Sessions`/`EmailTokens`, looked up by global hash) stay isolated cryptographically, not by RLS.

---

## Rules

The following repository rules must always be followed:

- Maintain project structure updated: everytime a folder/file of significance for the Project is add/updated/removed, maintain Projec Structure section updated.

- Architecture Decision Records: for each architectural decision made create and add a new adr file into `.docs/adr/<adr_decision_description>.md`, and add its pertinent entry into the ADR Index file `.docs/adr/adr_index.md`, if adr index file does not exist then create it.

- API Contracts: add every single API contract files into `.docs/api/`, and add its pertinent entry into the API Contracts Index file `.docs/api/api_contracts_index.md`, if api contracts index file does not existe then create it.

- Frontend components: when working on front-end components (Storefront or Admin UI), read `docs/reference/components.md` first and follow it.

- API endpoints: when adding or changing API endpoints in any service, read `docs/reference/api.md` first and follow it.

- Service list = ONE source: the canonical list of DB-owning services lives in `scripts/lib/services.sh` (name:path:port). `run-all.sh`, `dev-up.sh`, and `build-images.sh` all read it. When adding/removing a service, edit it there — and also the parallel lists that are NOT yet derived from it: the CI `docker` matrix + kind-deploy `df` map (`.github/workflows/ci.yml`), `deploy/migrator/Dockerfile` bundle loop, and `infra/postgres/init-databases.sql`. `scripts/e2e-verify.sh`'s L1 DB count derives from `init-databases.sql` (copy that pattern).

- New DB-owning service checklist (each item has bitten us): (1) `appsettings.json` + `Properties/launchSettings.json` with the service's DB connection, RabbitMq, InternalAuth dev key, and port (bare-run needs them — container config alone is not enough); (2) register the MassTransit outbox in `OnModelCreating` (`AddInboxStateEntity`/`AddOutboxMessageEntity`/`AddOutboxStateEntity`) or the service crash-loops at startup; (3) give each consumer a UNIQUE class name across services (the kebab queue name is derived from it — two `OrderConfirmedConsumer`s share one queue and compete); (4) add it to `scripts/lib/services.sh` + the non-derived lists above; (5) gateway route + cluster in both gateway appsettings; (6) `InitialCreate` migration. See ADR-0030.

- Image builds + memory: never `docker compose up --build` the full stack on a small Docker VM — it builds 13 .NET images in parallel and OOM-crashes the daemon. Use `scripts/build-images.sh` (bounded concurrency + memory preflight) or bare-run (`scripts/dev-up.sh`). When diagnosing a CI/deploy failure, read the failing step's RAW log first — kind-deploy's real cause was `no space left on device`, not the cascading probe timeouts it looked like.

- Regression test list: whenever a test is added, removed, or renamed — a unit/integration test (`*Tests.cs`), or a live end-to-end user-journey — update `scripts/e2e-verify.sh` so it stays the complete regression command: add/adjust the matching check **and** its line in the COVERAGE CHECKLIST header comment. Automated tests belong in the `A*` group, live full-stack flows in the `L*` group. The script must continue to pass (`scripts/e2e-verify.sh` and `--live`) after the change.

---

## Code Patterns

### Naming Conventions
- C#: standard .NET conventions (PascalCase types/methods, camelCase locals); projects named `3commerce.<Area>.<Layer>` (e.g. `3commerce.Ordering.Api`).
- Message contracts: past-tense events (`OrderConfirmed`, `ProductUpserted`), imperative commands (`AuthorizePayment`); records in `BuildingBlocks.Contracts`.
- TypeScript (Storefront): Next.js App Router conventions; components PascalCase, route folders kebab-case.

### File Organization
- Each service = `Api/` (endpoints, DI), `Domain/` (entities, invariants — no infrastructure references), `Infrastructure/` (EF, consumers, adapters), plus a test project.
- Shared code is plumbing and contracts only — **never shared domain logic**; duplication between services is preferred over coupling.
- Abstraction seams (one v1 implementation each): `ISupplierImporter`, `IPaymentProvider`, `ITaxStrategy`, `ISearchProvider`, `IAuthService`, `IEmailSender`. Do not add new speculative interfaces.

### Error Handling
- HTTP: RFC 7807 `application/problem+json` for all error responses.
- Messaging: MassTransit retry policy + error queues; consumers must be safe to redeliver (idempotent), never swallow poison messages silently.
- Money endpoints accept an `Idempotency-Key` header; ledger corrections are reversing entries, never updates.

### Domain invariants (non-negotiable)
- Money = integer minor units + ISO 4217 code; never floating point.
- Every ledger transaction balances (Σ debits = Σ credits) — DB-constraint enforced.
- Order line items carry a typed `FulfilmentType` (`Unassigned` allowed) + `BillingMode` + resolved `SupplierId` (ADR-0028) — one shared supply vocabulary in `BuildingBlocks.Contracts.Supply`, not the old per-service `FulfillmentSource` enum.
- Stock has a single owner: Fulfillment `InventoryItem`. Catalog `Variant.StockQuantity` is a read-model projection (`InventoryAvailabilityChanged`), never edited directly (ADR-0028).
- IDs are UUIDv7.

---

## Definition of Done

- Tests pass: `dotnet test 3commerce.sln` (incl. integration where touched)
- Lint/typecheck pass: `dotnet format --verify-no-changes`; storefront `npm run lint && npx tsc --noEmit`
- Docs updated: ADR for any architectural decision; API contract files for any endpoint change; Project Structure section if layout changed; PRD only on scope change (with explicit user approval)

---

## Testing

- **Run tests**: `dotnet test 3commerce.sln` (unit) · `dotnet test tests/ --filter Category=Integration` (Testcontainers; Docker required)
- **Test location**: per-service `tests/` projects; cross-service integration in root `tests/`
- **Pattern**: unit tests on Domain (no infra); integration tests against real Postgres + RabbitMQ via Testcontainers; saga tests via MassTransit test harness; property tests for the ledger balance invariant; chaos test (kill service mid-saga → terminal state on restart)

---

## Validation

```bash
dotnet build 3commerce.sln && dotnet format --verify-no-changes && dotnet test 3commerce.sln
# if storefront touched:
cd src/Storefront && npm run lint && npx tsc --noEmit && npm run build
```

---

## Key Files

| File | Purpose |
|------|---------|
| `docs/prd/PRD.md` | PRD index — load sections on demand only (see PRD Loading Rule) |
| `docs/prd/3commerce/04-mvp-scope.md` | Authoritative in/out-of-scope checklist |
| `docs/prd/3commerce/06-architecture.md` | Service boundaries, messaging rules, repo layout target |
| `docs/prd/3commerce/15-appendix.md` | Decision log (what was rejected and why) + launch blockers |
| `docker-compose.infra.yml` | Local Postgres + RabbitMQ (planned) |
| `src/BuildingBlocks/Contracts/` | Message contracts — version additively, never break consumers (planned) |
| `scripts/e2e-verify.sh` | Full regression command (automated + `--live` user journeys); keep current per the test-list rule |
| `.envrc` | direnv env vars (secrets stay in `.envrc.local`/user-secrets, git-ignored) |

---

## On-Demand Context

| Topic | File |
|-------|------|
| Building front-end components | `docs/reference/components.md` |
| Building API endpoints | `docs/reference/api.md` |
| Feature scope questions | `docs/prd/3commerce/04-mvp-scope.md` |
| FR-/NFR- requirement IDs | `docs/prd/3commerce/11-success-criteria.md` |
| Build order / current phase | `docs/prd/3commerce/12-implementation-phases.md` |
| Auth/security constraints | `docs/prd/3commerce/09-security-configuration.md` |
| Endpoint conventions | `docs/prd/3commerce/10-api-specification.md` |
| Deferred features ("not now" list) | `docs/prd/3commerce/13-future-considerations.md` |

---

## Boundaries (Do NOT)

- Don’t change CI/infra without explicit instruction
- Don’t refactor unrelated modules during feature work
- Don’t introduce new dependencies without justification
- No secrets in logs or commits
- Don’t query another service's database — cross-service data arrives via events only
- Don’t put domain logic in `BuildingBlocks` — contracts and plumbing only
- Don’t hand-roll cryptography or session-token generation — vetted libraries only (Argon2id, CSPRNG)
- Don’t let card data touch any server — Stripe Payment Element only (SAQ-A)
- Don’t call Stripe/issue refunds outside the saga/ledger path — the ledger must never silently diverge
- Don’t build out-of-scope features (MFA, Polar adapter, k8s, search engines, discounts) — they're deferred in PRD §13, not forgotten
- Don’t auto-load the full PRD — follow the PRD Loading Rule

---

## Notes

- **Canonical ports:** Gateway 8080 · Identity 5101 · Catalog 5102 · Ordering 5103 · Payments 5104 · Fulfillment 5105 · Support 5106 · Entity 5107 · Storefront 3000 · Admin 5200 · SupplierPortal 5300 · Postgres 5432 · RabbitMQ 5672 (UI 15672, guest/guest).
- **Namespaces:** projects are `3commerce.*` but namespaces are `ThreeCommerce.*` (C# forbids digit-leading namespaces; mapped in `Directory.Build.props`).
- **MassTransit is pinned to 8.x** (open-source line) — v9+ is commercially licensed; do not bump without a license decision (see `Directory.Packages.props`).
- **Local tooling:** .NET SDK lives in `~/.dotnet` (user-local install; PATH/DOTNET_ROOT via `.envrc`/direnv). Docker runs via **colima** (`colima start`) — Docker Desktop is installed but its daemon doesn't start headlessly.
- Service health endpoints (`/health/live|ready`) are internal-only; the gateway returns 404 for any `/api/*/health*` path.
- Stripe runs **test mode only** and Xero against a demo org until a legal entity exists (launch gate, not build gate) — see PRD Appendix B.
- Currency is config (`STORE_CURRENCY`), tax is `ITaxStrategy` — jurisdiction is unknown until company registration; never hardcode either.
- Microservices were chosen knowingly for learning value (PRD decision #5); keep each service internally simple — complexity budget is spent on the seams.
- `docs/adr/` exists with ADRs 0001–0020 (backfilled from the PRD decision log) + `adr_index.md`; new ADRs continue the numbering. `docs/api/` doesn't exist yet — create it (with its index file) on first use per the Rules section.
