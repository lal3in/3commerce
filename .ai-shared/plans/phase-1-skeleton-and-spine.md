# Feature: Phase 1 — Skeleton & Spine (solution layout, infra, messaging spine, gateway, observability, CI)

The following plan should be complete, but its important that you validate documentation and codebase patterns and task sanity before you start implementing.

Pay special attention to naming of existing utils types and models. Import from the right files etc.

## Feature Description

Scaffold the entire 3commerce backbone so that every infrastructure pattern is proven on trivial functionality before any business feature exists: 6 service stubs + YARP gateway + Notifications worker + BuildingBlocks, Postgres/RabbitMQ via docker compose, MassTransit with EF transactional outbox demonstrated end-to-end on a ping-pong event, OpenTelemetry tracing across HTTP → service → RabbitMQ consumer, and CI that builds, tests, and docker-builds everything. No later phase should ever fight plumbing.

## User Story

As the builder (solo C# developer)
I want a fully wired distributed skeleton with one trivial event flowing through outbox → RabbitMQ → consumer under a single trace
So that Phases 2–4 implement business logic on proven seams instead of debugging infrastructure mid-feature.

## Problem Statement

The repo contains documentation only (PRD, ADRs, AGENTS.md, reference guides) — zero code. Every PRD phase depends on solution structure, messaging, data isolation, gateway routing, and observability conventions that do not exist yet.

## Solution Statement

Create the canonical layout from `docs/prd/3commerce/06-architecture.md` exactly (AGENTS.md mandates reality match it), with a ping-pong contract proving the outbox/saga substrate, health endpoints per service, database-per-service provisioning, YARP config routing, OTel wiring in BuildingBlocks, and a GitHub Actions pipeline.

## Feature Metadata

**Feature Type**: New Capability (greenfield scaffold)
**Estimated Complexity**: Medium (no business logic, but many moving parts)
**Primary Systems Affected**: everything — this creates the system
**Dependencies**: .NET 10 SDK, Docker, MassTransit v8, YARP, EF Core 10 + Npgsql, OpenTelemetry, xUnit + Testcontainers

---

## CONTEXT REFERENCES

### Relevant Codebase Files IMPORTANT: YOU MUST READ THESE FILES BEFORE IMPLEMENTING!

- `AGENTS.md` — Why: binding conventions (project naming, validation commands, boundaries, Definition of Done). Update its Project Structure note ("pre-code") when done.
- `docs/prd/3commerce/06-architecture.md` — Why: repo layout target + messaging/data rules this plan instantiates.
- `docs/prd/3commerce/12-implementation-phases.md` — Why: Phase 1 deliverables and validation gates.
- `docs/adr/0007-masstransit-rabbitmq-outbox-sagas.md` — Why: outbox/idempotency requirements proven here.
- `docs/adr/0008-database-per-service-single-postgres.md` — Why: six-databases-one-container provisioning.
- `docs/adr/0009-local-dev-bare-dotnet-run.md` — Why: compose file scope (infra only).
- `docs/adr/0011-yarp-gateway-single-origin.md` — Why: gateway responsibilities (route, later auth, header stripping).
- `docs/reference/api.md` — Why: endpoint organization (MapGroup, TypedResults, ProblemDetails, health checks) applied to the stub endpoints.

### New Files to Create

- `3commerce.sln`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`
- `docker-compose.infra.yml`, `infra/postgres/init-databases.sql`
- `src/BuildingBlocks/Contracts/` (`3commerce.BuildingBlocks.Contracts.csproj`, `Ping/PingRequested.cs`, `Ping/PongResponded.cs`)
- `src/BuildingBlocks/Infrastructure/` (`3commerce.BuildingBlocks.Infrastructure.csproj`, `Messaging/MassTransitExtensions.cs`, `Observability/OtelExtensions.cs`, `Web/ProblemDetailsExtensions.cs`, `Web/HealthEndpoints.cs`)
- `src/Gateway/` (`3commerce.Gateway.csproj`, `Program.cs`, `appsettings.json` with YARP routes)
- `src/Services/{Identity,Catalog,Ordering,Payments,Fulfillment,Support}/` each: `Api/`, `Domain/`, `Infrastructure/` projects + `tests/` project, DbContext + initial migration, `Program.cs`, health endpoints
- `src/Workers/Notifications/` (worker stub consuming `PongResponded`, logs only)
- `tests/3commerce.IntegrationTests/` (`SpineTests.cs` — outbox redelivery test)
- `.github/workflows/ci.yml`
- `scripts/run-all.sh` (tmux/parallel `dotnet run` helper, optional convenience)

### Relevant Documentation YOU SHOULD READ THESE BEFORE IMPLEMENTING!

- [MassTransit — EF Core transactional outbox](https://masstransit.io/documentation/patterns/transactional-outbox)
  - Why: exact DbContext registration (`AddInboxStateEntity`, `AddOutboxStateEntity`, `AddOutboxMessageEntity`) and bus outbox config — the core Phase 1 proof.
- [MassTransit — RabbitMQ configuration](https://masstransit.io/documentation/configuration/transports/rabbitmq)
  - Why: endpoint naming conventions, retry policy.
- [YARP — configuration files](https://microsoft.github.io/reverse-proxy/articles/config-files.html)
  - Why: route/cluster JSON schema for `/api/{service}/*` path-prefix routing + `PathRemovePrefix` transform.
- [OpenTelemetry .NET — getting started](https://opentelemetry.io/docs/languages/dotnet/getting-started/)
  - Why: tracer/meter provider setup; MassTransit emits `DiagnosticSource` activity — wire `AddSource("MassTransit")`.
- [Testcontainers for .NET — PostgreSQL + RabbitMQ modules](https://dotnet.testcontainers.org/modules/)
  - Why: integration test fixtures.
- [.NET docker-compose healthchecks for RabbitMQ/Postgres](https://docs.docker.com/compose/how-tos/startup-order/)
  - Why: `depends_on: condition: service_healthy` so init scripts finish before services connect.

### Patterns to Follow

**Project naming (AGENTS.md):** projects named `3commerce.<Area>.<Layer>`.

**GOTCHA — namespaces cannot start with a digit.** Set in `Directory.Build.props`:

```xml
<PropertyGroup>
  <RootNamespace>$(MSBuildProjectName.Replace("3commerce", "ThreeCommerce"))</RootNamespace>
  <Nullable>enable</Nullable>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  <ImplicitUsings>enable</ImplicitUsings>
</PropertyGroup>
```

Assemblies/projects stay `3commerce.*`; namespaces are `ThreeCommerce.*`. Document this at the top of Directory.Build.props.

**Endpoint organization (docs/reference/api.md §1):** static `XEndpoints` class + `MapGroup`, named static handlers, `TypedResults`, `CancellationToken` param, `AddProblemDetails()` in every service.

**Message contracts (docs/reference/api.md §8):** records in Contracts, additive versioning. Phase 1 sample:

```csharp
namespace ThreeCommerce.BuildingBlocks.Contracts.Ping;
public record PingRequested(Guid PingId, DateTimeOffset RequestedAt);
public record PongResponded(Guid PingId, string RespondedBy);
```

**Port allocation (canonical — reuse in all later phases):** Gateway `8080`; Identity `5101`; Catalog `5102`; Ordering `5103`; Payments `5104`; Fulfillment `5105`; Support `5106`; Notifications worker (no HTTP); Postgres `5432`; RabbitMQ `5672`/UI `15672`; Storefront `3000` (Phase 2); Admin `5200` (Phase 4).

**Database names:** `identity_db`, `catalog_db`, `ordering_db`, `payments_db`, `fulfillment_db`, `support_db` — created by `infra/postgres/init-databases.sql` mounted at `/docker-entrypoint-initdb.d/`.

---

## IMPLEMENTATION PLAN

### Phase 1: Foundation

Solution scaffolding, central package management, infra compose.

### Phase 2: Core Implementation

BuildingBlocks (messaging/OTel/web helpers), per-service skeletons with DbContexts + outbox tables + health endpoints.

### Phase 3: Integration

Gateway routing, ping-pong event across two services through outbox, Notifications worker consuming.

### Phase 4: Testing & Validation

Testcontainers spine test (incl. consumer-down redelivery), CI workflow, trace verification.

---

## STEP-BY-STEP TASKS

### 1. CREATE solution + build props

- **IMPLEMENT**: `dotnet new sln -n 3commerce`; `Directory.Build.props` (RootNamespace gotcha above), `Directory.Packages.props` (central versions: MassTransit, MassTransit.RabbitMQ, MassTransit.EntityFrameworkCore, Npgsql.EntityFrameworkCore.PostgreSQL, Yarp.ReverseProxy, OpenTelemetry.*, xunit, Testcontainers.PostgreSql, Testcontainers.RabbitMq), `.editorconfig` (dotnet format baseline).
- **GOTCHA**: pin one MassTransit version family centrally; mixed versions break saga/outbox wiring.
- **VALIDATE**: `dotnet build 3commerce.sln` (empty sln builds)

### 2. CREATE docker-compose.infra.yml + infra/postgres/init-databases.sql

- **IMPLEMENT**: Postgres 17 (volume, healthcheck `pg_isready`, init SQL creating the six databases + per-service users) and RabbitMQ 4-management (healthcheck `rabbitmq-diagnostics ping`). No service containers (ADR-0009).
- **VALIDATE**: `docker compose -f docker-compose.infra.yml up -d && docker compose -f docker-compose.infra.yml ps` then `docker exec $(docker ps -qf name=postgres) psql -U postgres -c '\l' | grep -c '_db'` → `6`

### 3. CREATE src/BuildingBlocks/Contracts project

- **IMPLEMENT**: classlib `3commerce.BuildingBlocks.Contracts`; `Ping/PingRequested.cs`, `Ping/PongResponded.cs` records.
- **VALIDATE**: `dotnet build src/BuildingBlocks/Contracts`

### 4. CREATE src/BuildingBlocks/Infrastructure project

- **IMPLEMENT**: classlib `3commerce.BuildingBlocks.Infrastructure` with:
  - `Messaging/MassTransitExtensions.cs`: `AddServiceBus<TDbContext>(this IServiceCollection, IConfiguration, Action<IBusRegistrationConfigurator>?)` — RabbitMQ transport, kebab-case endpoint names, retry (3x incremental), `AddEntityFrameworkOutbox<TDbContext>(o => { o.UsePostgres(); o.UseBusOutbox(); })`, inbox enabled.
  - `Observability/OtelExtensions.cs`: `AddServiceTelemetry(serviceName)` — ASP.NET Core + HttpClient + EF + `AddSource("MassTransit")`, OTLP exporter env-configurable, console exporter fallback.
  - `Web/ProblemDetailsExtensions.cs`: `AddProblemDetails` + exception handler returning RFC 9457 with `traceId`.
  - `Web/HealthEndpoints.cs`: `MapServiceHealth()` → `/health/live` (self) + `/health/ready` (DbContext + bus).
- **PATTERN**: docs/reference/api.md §4, §7.
- **VALIDATE**: `dotnet build src/BuildingBlocks/Infrastructure`

### 5. CREATE the six service skeletons (repeat per service: Identity, Catalog, Ordering, Payments, Fulfillment, Support)

- **IMPLEMENT**: per service three projects `3commerce.<Svc>.Api` (web), `.Domain` (classlib, no infra refs), `.Infrastructure` (classlib: `<Svc>DbContext` with MassTransit inbox/outbox entities, initial EF migration) + `tests/3commerce.<Svc>.Tests` (xunit). `Program.cs`: minimal API + `AddServiceBus<TDbContext>` + `AddServiceTelemetry` + ProblemDetails + `MapServiceHealth()`; `launchSettings.json` pinned to the canonical port; connection string to its own database only (NFR-4).
- **GOTCHA**: outbox requires the three MassTransit entity registrations in `OnModelCreating` (`AddInboxStateEntity()` etc.) — migration must include them.
- **VALIDATE**: `dotnet ef database update --project src/Services/<Svc>/Infrastructure --startup-project src/Services/<Svc>/Api` then `curl -fsS localhost:51XX/health/ready` per service

### 6. CREATE src/Workers/Notifications worker stub

- **IMPLEMENT**: worker template, `AddServiceBus` (no DbContext overload — add a parameterless variant using in-memory outbox NONE; plain consumer), `PongRespondedConsumer` that logs structured message. No email yet (Phase 2).
- **VALIDATE**: `dotnet run --project src/Workers/Notifications` starts and binds queue (check `curl -u guest:guest localhost:15672/api/queues | grep pong`)

### 7. ADD ping-pong spine across Catalog → Ordering

- **IMPLEMENT**: in Catalog Api: `PingEndpoints.MapPing` → `POST /ping` writes a `PingRecord` row AND publishes `PingRequested` **through the outbox in one transaction**. In Ordering Infrastructure: `PingRequestedConsumer` (idempotent via inbox) publishes `PongResponded`. Notifications logs it.
- **PATTERN**: this is the template every future event flow copies — outbox write+publish, inbox-idempotent consume.
- **VALIDATE**: with infra + Catalog + Ordering + Notifications running: `curl -X POST localhost:5102/ping` → Notifications log line contains the PingId within 5s

### 8. CREATE src/Gateway (YARP)

- **IMPLEMENT**: `3commerce.Gateway` web project; YARP from `appsettings.json`: route per service `api/{service}/{**catch-all}` → cluster `http://localhost:51XX` with `PathRemovePrefix: /api/{service}`; request transform stripping inbound `X-Internal-Claims`; rate-limiter middleware registered with permissive defaults (real limits Phase 2); `AddServiceTelemetry("gateway")`.
- **GOTCHA**: do NOT add auth yet — session validation lands in Phase 2 with Identity; leave a clearly marked `// PHASE2: session validation middleware` insertion point.
- **VALIDATE**: `curl -X POST localhost:8080/api/catalog/ping` → 200 and Notifications logs the pong

### 9. CREATE tests/3commerce.IntegrationTests spine test

- **IMPLEMENT**: xunit + Testcontainers (PostgreSql, RabbitMq modules) + `WebApplicationFactory` for Catalog/Ordering. Tests: (a) `Ping_flows_to_pong` end-to-end; (b) `Outbox_survives_consumer_down`: post ping with Ordering consumer stopped (don't start its host), assert outbox row persisted, start Ordering host, assert pong arrives — this is the NFR-2 seed; (c) `Duplicate_delivery_is_idempotent`: deliver the same `PingRequested` twice, assert single pong.
- **VALIDATE**: `dotnet test tests/3commerce.IntegrationTests --filter Category=Integration` (Docker running)

### 10. CREATE Dockerfiles per service + gateway + worker

- **IMPLEMENT**: standard multi-stage .NET 10 Dockerfile per runnable project (8 total); build-only verification (deploy is post-MVP, ADR-0009).
- **VALIDATE**: `docker build -f src/Gateway/Dockerfile .` (spot-check one; CI checks all)

### 11. CREATE .github/workflows/ci.yml

- **IMPLEMENT**: on PR/push: setup .NET 10 → `dotnet build` → `dotnet format --verify-no-changes` → `dotnet test` (unit) → integration job with Docker → matrix docker-build of all 8 Dockerfiles. Add `dotnet list package --vulnerable` step (PRD security scope).
- **VALIDATE**: `act -n` if available, else push to a branch and confirm green run

### 12. UPDATE AGENTS.md + create ADR if deviations occurred

- **IMPLEMENT**: remove the "pre-code" status note's caveat (structure now real); record port table in AGENTS.md Notes; if any implementation deviated from ADRs, write a new ADR + index entry per repo Rules.
- **VALIDATE**: `grep -n "pre-code" AGENTS.md` returns updated wording

---

## TESTING STRATEGY

### Unit Tests
Minimal in Phase 1 (no business logic): BuildingBlocks helpers (ProblemDetails shape, health endpoint registration) via `WebApplicationFactory` smoke tests.

### Integration Tests
The three spine tests in Task 9 are the heart of Phase 1 — they prove outbox atomicity, redelivery recovery, and inbox idempotency on real Postgres + RabbitMQ via Testcontainers.

### Edge Cases
- RabbitMQ down at service start → service stays alive, `/health/ready` red, recovers on broker return.
- Postgres init script idempotence (volume already initialized → no failure).
- Duplicate message delivery (test 9c).

---

## VALIDATION COMMANDS

### Level 1: Syntax & Style
```bash
dotnet build 3commerce.sln && dotnet format --verify-no-changes
```

### Level 2: Unit Tests
```bash
dotnet test 3commerce.sln --filter Category!=Integration
```

### Level 3: Integration Tests
```bash
docker info >/dev/null && dotnet test tests/3commerce.IntegrationTests --filter Category=Integration
```

### Level 4: Manual Validation
```bash
docker compose -f docker-compose.infra.yml up -d
# start Gateway, Catalog, Ordering, Notifications (4 terminals or scripts/run-all.sh)
curl -fsS -X POST localhost:8080/api/catalog/ping        # 200
curl -fsS localhost:8080/api/ordering/health/ready 2>&1 || true  # health NOT proxied publicly — expect 404
for p in 5101 5102 5103 5104 5105 5106; do curl -fsS localhost:$p/health/ready; done
# Trace check: console/OTLP output shows one trace id spanning gateway→catalog→rabbitmq→ordering
```

### Level 5: Additional Validation (Optional)
RabbitMQ management UI (localhost:15672, guest/guest): queues bound, zero dead-letters after ping test.

---

## ACCEPTANCE CRITERIA

- [ ] Repo layout matches `docs/prd/3commerce/06-architecture.md` (AGENTS.md updated to confirm)
- [ ] One `curl` through the gateway produces a single distributed trace including a RabbitMQ hop (PRD Phase-1 gate)
- [ ] Outbox redelivery test green: consumer killed and restarted → message delivered (NFR-2 seed)
- [ ] Idempotent consume test green (NFR-3 seed)
- [ ] Six databases, six DbContexts, six migration histories; no service references another's connection string (NFR-4)
- [ ] All 8 Dockerfiles build in CI; format/build/test jobs green
- [ ] All validation commands pass with zero errors

## COMPLETION CHECKLIST

- [ ] All tasks completed in order
- [ ] Each task validation passed immediately
- [ ] Full test suite passes (unit + integration)
- [ ] No linting or type checking errors
- [ ] Manual ping-pong + trace confirmed
- [ ] `.ai-shared/plans/plan_status_executions.md` updated per task

## NOTES

- This plan deliberately contains zero business logic — resist adding any; Phase 2 starts auth/catalog.
- The `3commerce` → `ThreeCommerce` namespace mapping is the one naming decision not explicit in AGENTS.md; if you prefer a different root namespace, decide once here and record it in AGENTS.md Code Patterns.
- Health endpoints are intentionally NOT routed by the gateway (internal only).
- Confidence: 8/10 — greenfield, no integration unknowns except MassTransit outbox + .NET 10 version pairing; validate package versions at execution time.
