# Production Platform Architecture Roadmap Plan

Last Modified Date-Time: 2026-06-28
Status: Planned
Branch when executing: `main` or feature branch `feat/production-platform-roadmap`

## Goal

Create an implementation-ready roadmap for the production platform architecture items recently evaluated:

1. Harden **YARP** as the current API gateway and reverse proxy.
2. Add **PgBouncer** before broad horizontal service scale-out.
3. Audit and add **PostgreSQL platform indexes/features** where measured queries justify them.
4. Add **Kafka** as a durable event-stream lane beside RabbitMQ, not as a replacement.
5. Plan **logical replication / CDC** after event-stream ownership is settled.
6. Design a future **tenant shard map** before implementing sharding.
7. Defer **Kong** unless public API-management becomes a product requirement.

This plan is documentation and sequencing only. It does not implement code.

## Non-goals

- Do not replace RabbitMQ/MassTransit with Kafka.
- Do not replace YARP with Kong now.
- Do not shard databases now.
- Do not add logical replication before event ownership and stream taxonomy are documented.
- Do not bypass service-owned databases or RLS.
- Do not introduce speculative infrastructure that is not tied to a production scaling/operational need.

## Current codebase evidence

### YARP / gateway

- `src/Gateway/Program.cs` wires `AddReverseProxy().LoadFromConfig(...)` and `app.MapReverseProxy()`.
- `src/Gateway/appsettings.json:10` defines `ReverseProxy` routes for service prefixes.
- `src/Gateway/appsettings.json:162` defines each cluster with a single destination.
- `src/Gateway/appsettings.Container.json:5` overrides container cluster destinations, also one destination per service.
- `docs/adr/0011-yarp-gateway-single-origin.md` accepts YARP as the single public origin because it can run custom session validation, internal-claims minting, rate limits, and header hygiene.
- `Directory.Packages.props:27` centrally pins `Yarp.ReverseProxy`.

### RabbitMQ / MassTransit

- `src/BuildingBlocks/Infrastructure/Messaging/MassTransitExtensions.cs:15` wires DB-owning services to MassTransit with EF transactional outbox.
- `src/BuildingBlocks/Infrastructure/Messaging/MassTransitExtensions.cs:37` adds consumer EF outbox/inbox and retry.
- `src/BuildingBlocks/Infrastructure/Messaging/MassTransitExtensions.cs:45` uses RabbitMQ as the operational transport.
- `Directory.Packages.props:4` explicitly pins MassTransit 8.x and warns not to move to v9+ without a license decision.
- `docs/adr/0007-masstransit-rabbitmq-outbox-sagas.md` accepts RabbitMQ/MassTransit/outbox/sagas and defers Kafka/event sourcing from v1.
- `docker-compose.infra.yml:23` runs RabbitMQ for local infrastructure.

### PostgreSQL

- `docker-compose.infra.yml:6` runs PostgreSQL 17.
- `docs/adr/0008-database-per-service-single-postgres.md` accepts one PostgreSQL instance with one database/login per service.
- `docs/adr/0022-named-schema-per-service.md` adds named service schemas inside each service database.
- `docs/adr/0024-tenant-isolation-postgres-rls.md` requires tenant isolation through PostgreSQL RLS and application tenant scope.
- No PgBouncer service is currently present in compose.
- No shard-map service/table is currently part of the platform architecture.
- No explicit logical replication or CDC pipeline is currently present.

### Kong / external API management

- There is no Kong configuration in the repository.
- Current gateway requirements are custom session validation, tenant/domain resolution, internal claims, rate limiting, and header hygiene; these are already implemented around YARP.

## External research anchors

- Microsoft YARP docs describe destination health checks and load balancing policies, including proactive destination probing.
- MassTransit Kafka support exists through a Kafka rider / topic endpoints, but this repository's existing reliability center is the EF transactional outbox on the RabbitMQ bus; Kafka should be introduced with a committed stream-outbox/relay pattern.
- PgBouncer supports session, transaction, and statement pooling; transaction pooling is the likely scale-out target but must be validated against EF/Npgsql behavior and session-state assumptions.
- PostgreSQL docs support partial indexes and expression indexes for targeted query shapes, and logical replication through publication/subscription.
- Kong provides API-management plugins such as rate limiting, but adopting it would add another gateway/control-plane layer and should wait for a public API-management requirement.

## Recommended adoption order

### Phase pplat_1 — YARP production hardening

Why first: YARP is already the single public origin. Hardening it gives immediate production value without changing service boundaries.

Implementation-ready scope:

- Add a gateway ADR update or new ADR documenting production YARP posture.
- Add route/cluster policy conventions:
  - active health checks against service `/health/ready` endpoints for production clusters;
  - load-balancing policy for multi-destination clusters;
  - conservative retry policy only for safe/idempotent methods where configured;
  - strict timeout policy per route class.
- Make production cluster destination lists environment-driven so multiple service replicas can be configured without code changes.
- Keep app/business retries in services and broker consumers; avoid gateway retries around payment/refund mutating calls unless idempotency is proven.
- Add tests/config validation for route coverage and blocked `/api/*/health*` behavior.

Validation:

```bash
dotnet build src/Gateway/3commerce.Gateway.csproj
dotnet test 3commerce.sln --filter Gateway
helm template 3commerce deploy/helm/3commerce -f deploy/helm/3commerce/values-prod.yaml >/tmp/3commerce-prod-rendered.yaml
scripts/e2e-verify.sh
```

Acceptance criteria:

- Gateway config supports more than one destination per cluster.
- Health checks and load-balancing are documented and config-rendered.
- Mutating-money routes are not blindly retried.
- Existing storefront/admin flows are unchanged.

### Phase pplat_2 — PgBouncer connection pooling

Why second: horizontal service replicas multiply PostgreSQL connections quickly across 13 DB-owning services.

Implementation-ready scope:

- Add a PgBouncer ADR covering pooling mode, transaction semantics, prepared statement considerations, observability, and failover.
- Add PgBouncer to local/containerized deploy as an optional profile first.
- Configure per-service connection strings to route through PgBouncer when enabled.
- Validate EF Core migrations/bundles still connect directly to Postgres or have a documented PgBouncer-safe mode.
- Add pool metrics to observability/runbooks.

Validation:

```bash
docker compose config
dotnet test tests/3commerce.IntegrationTests/3commerce.IntegrationTests.csproj --filter "Category=Integration"
scripts/e2e-verify.sh --live
```

Acceptance criteria:

- Services can run through PgBouncer in dev/container mode.
- Migration path is documented and safe.
- No RLS tenant-scope behavior regresses.
- Operator docs explain when to use direct Postgres vs PgBouncer.

### Phase pplat_3 — PostgreSQL partial/functional index audit

Why third: indexes should be driven by actual query paths and slow-query evidence, not broad guesswork.

Implementation-ready scope:

- Inventory high-traffic queries per service: storefront catalog/search, checkout/cart reads, admin order/payment pages, audit/workflow listings, usage/subscription reads.
- Add `EXPLAIN (ANALYZE, BUFFERS)` capture workflow for seeded realistic data.
- Add targeted partial indexes for common filtered states such as active/unpublished/pending records.
- Add expression indexes only where exact query predicates match normalized expressions.
- Confirm RLS and tenant filters are represented in index prefixes where needed.
- Document index ownership in migrations and relevant API/help docs.

Validation:

```bash
dotnet test 3commerce.sln
dotnet test tests/3commerce.IntegrationTests/3commerce.IntegrationTests.csproj --filter "Category=Integration"
scripts/dev-up.sh --with-frontends --data dummy
# capture EXPLAIN plans manually/with helper script once added
```

Acceptance criteria:

- Each new index has a named query/use case and before/after plan evidence.
- No speculative wide indexes are added.
- Migrations are service-owned and named-schema compliant.

### Phase pplat_4 — Kafka durable event-stream design and first slice

Why fourth: Kafka is useful, but only after operational workflow boundaries are protected.

Implementation-ready scope:

- Adopt the existing `.ai-shared/plans/production-grade-messaging-eventing-architecture.md` as the detailed Kafka/messaging sub-plan.
- Write ADR defining the two-lane model:
  - RabbitMQ/MassTransit = commands, sagas, workflow, retryable operational messages.
  - Kafka = committed facts, analytics, audit projection, replay, data lake/warehouse feed.
- Start with event envelope/contracts, producer abstraction, stream outbox/relay, one low-risk producer, and one replay consumer proof.
- Prefer managed Kafka for production; optional single-node Kafka only for dev/testing.

Validation:

```bash
dotnet build 3commerce.sln
dotnet test 3commerce.sln --filter "Kafka|StreamOutbox|Replay|Messaging"
docker compose config
scripts/e2e-verify.sh
```

Acceptance criteria:

- Checkout/payment/saga flows do not depend on Kafka availability.
- Stream facts are committed through outbox/relay, not dual-written.
- Topic taxonomy, privacy class, retention, schema evolution, and partition keys are documented.

### Phase pplat_5 — Logical replication / CDC design

Why fifth: CDC is valuable after event ownership and stream semantics are clear; otherwise it can leak internal tables and undermine service contracts.

Implementation-ready scope:

- Write ADR for CDC boundaries.
- Decide whether CDC feeds are for operational replication, analytics bootstrap, warehouse export, or disaster-recovery support.
- Prefer service-owned event streams for domain facts; use logical replication for infrastructure movement only where table-level replication is explicitly justified.
- Define publication ownership, replication slots, lag monitoring, WAL retention alarms, and redaction/filtering boundaries.
- Never expose service-owned raw tables as a cross-service integration API.

Validation:

```bash
docker compose config
helm template 3commerce deploy/helm/3commerce -f deploy/helm/3commerce/values-prod.yaml >/tmp/3commerce-prod-rendered.yaml
```

Acceptance criteria:

- CDC has a documented consumer, data classification, retention, and operational owner.
- WAL/slot monitoring is present before any long-lived slot is enabled.
- Service-owned DB isolation remains intact.

### Phase pplat_6 — Tenant shard-map design

Why sixth: sharding changes every data path and should be designed early but implemented only when metrics demand it.

Implementation-ready scope:

- Write ADR for shard-map model: tenant → region → shard group → service database endpoint.
- Define tenant-placement rules, no-cross-region-move assumption, bootstrap/default shard behavior, and future tenant migration constraints.
- Update service connection-resolution design without implementing broad runtime routing yet.
- Decide how global secret-keyed lookups remain safe when tenant location is unknown.
- Identify which services are shard-ready and which need preparatory work.

Validation:

```bash
dotnet test 3commerce.sln --filter Tenancy
```

Acceptance criteria:

- Sharding is documented as future-ready design, not enabled behavior.
- RLS tenant isolation remains the first isolation layer inside each shard.
- No cross-shard joins are introduced.

### Phase pplat_7 — Kong decision gate

Why last: Kong is not needed for the current product unless API-management becomes a product feature.

Decision gate to revisit Kong:

- Public partner/developer API program.
- API keys/plans/quotas as product features.
- Developer portal requirement.
- Multi-consumer API monetization/analytics requirement.
- Need for a centralized gateway fleet independent of .NET customization.

Implementation-ready scope if gate triggers:

- Write ADR comparing Kong in front of YARP, Kong replacing YARP, and YARP-only.
- Preserve YARP if custom session/tenant/internal-claims logic remains easier there.
- Prototype only after requirements exist.

Acceptance criteria:

- Until the gate triggers, no Kong infra is added.
- YARP remains the gateway of record.

## Work breakdown for tracker

| Task_ID | Task_Name | Phase | Initial Status | Comments |
|---|---|---|---|---|
| pplat_1 | YARP production hardening ADR/config | Phase 1 gateway | pending | Highest near-term value; no service-boundary change. |
| pplat_2 | PgBouncer ADR and optional deploy profile | Phase 2 database connections | pending | Add before raising app replicas broadly. |
| pplat_3 | PostgreSQL index audit | Phase 3 database performance | pending | Evidence-driven partial/expression indexes only. |
| pplat_4 | Kafka durable stream lane | Phase 4 event streaming | pending | Use detailed messaging plan; RabbitMQ remains operational bus. |
| pplat_5 | Logical replication / CDC design | Phase 5 data movement | pending | After Kafka/event ownership. |
| pplat_6 | Tenant shard-map design | Phase 6 scale design | pending | Design now, implement later. |
| pplat_7 | Kong API-management gate | Phase 7 API management | pending | Defer unless product/API-management requirements emerge. |

## Risks and mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| Gateway retries duplicate mutating operations | Ledger/payment divergence | Route-specific retry policy; require idempotency keys before retrying writes |
| PgBouncer transaction pooling breaks session assumptions | RLS or prepared statement failures | Test tenant-scope transactions and Npgsql/EF behavior before default enablement |
| Speculative indexes bloat writes | Slower mutations and migrations | Require query evidence and review per index |
| Kafka becomes operational dependency | Checkout fragility | Core workflows remain RabbitMQ/outbox; stream relay can lag safely |
| CDC leaks private service tables | Contract and privacy breach | ADR boundary; domain facts via Kafka first; CDC only for explicit infra/data movement |
| Sharding implemented too early | Complexity explosion | Design only until production volume proves need |
| Kong duplicates YARP responsibilities | More moving parts | Decision gate tied to public API-management requirements |

## Overall acceptance criteria

- A new ADR or ADR update exists for each implemented architecture decision.
- YARP remains the active gateway and is production-hardened before Kong is reconsidered.
- PgBouncer is available before broad service replica increases.
- PostgreSQL indexes are evidence-backed.
- Kafka is introduced as a second lane for committed facts/replay, not as a RabbitMQ replacement.
- CDC and sharding remain design-gated until clear operational need.
- Tracker rows in `.ai-shared/plans/plan_status_executions.md` reflect task status as work proceeds.
- `scripts/e2e-verify.sh` remains the full regression command whenever tests or live checks are added.

## Confidence score

**0.86**

Reasoning: The current repository already has the right seams for gateway centralization, service-owned databases, MassTransit outbox/inbox, RLS, and observability. The main uncertainty is operational capacity for additional infrastructure in CI/local development; the roadmap therefore sequences lower-risk hardening before Kafka/CDC/sharding.
