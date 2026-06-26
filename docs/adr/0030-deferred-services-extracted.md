# ADR-0030: Phase 5–7 deferred services extracted into standalone services

## Status

Accepted — implemented once CI was restored (Marketing, Pricing, Audit, Workflow, Entitlement, Usage).
Supersedes the "services deferred" half of [ADR-0029](./0029-phase-6-compliance-ops-primitives.md) for
these six; the capability-first primitives it describes remain the substrate.

## Context

ADR-0028 (composable supply) and ADR-0029 (capability-first) deliberately built several Phase 5–7
capabilities **inside** existing services or `BuildingBlocks` while CI could not validate new
DB-owning services end to end (compose/Helm/CI/migrator). With CI restored, the seams those ADRs left
made extraction a project move rather than a redesign. Two of the six (Entitlement, Usage) lived inside
Fulfillment; the other four were either standalone domains (Marketing, Pricing) or event projections
(Audit, Workflow).

## Decision

Promote the six capabilities into standalone DB-owning services, each following the established service
shape: a named schema per service (ADR-0022), a single Postgres database per service (ADR-0008), the
MassTransit EF outbox, internal-claims auth behind the YARP gateway (ADR-0011/0012), an `InitialCreate`
migration, a Dockerfile, and full deploy wiring (compose, gateway route, migrator, CI image, Helm).

| Service | Gateway | Port | Pattern | Owns |
|---|---|---|---|---|
| Marketing | `/api/marketing` | 5108 | standalone domain | Campaigns, short links (mt5_1/5_3) |
| Pricing | `/api/pricing` | 5109 | standalone domain | Prices + graduated tiers (mt7_1) |
| Audit | `/api/audit` | 5110 | event projection | Central searchable audit copy (mt6_1) |
| Workflow | `/api/workflow` | 5111 | event projection | Scheduled-job run history (mt6_3) |
| Entitlement | `/api/entitlement` | 5112 | extracted from Fulfillment | Digital-line access (mt7_2/7_6) |
| Usage | `/api/usage` | 5113 | extracted from Fulfillment | Metered balances + overage billing (mt7_4/7_5) |

**Projection services (Audit, Workflow)** consume an event the owning capability now optionally
publishes: `AuditRecorder` publishes `AuditEntryRecorded`, `JobExecutor` publishes `JobRunRecorded`.
The publisher is an **optional** constructor dependency — DI injects `IPublishEndpoint` in a host, while
unit tests construct without one (no-op). The local log / job-run store stays authoritative; the service
is a cross-service read model.

**Extraction services (Entitlement, Usage)** move the aggregate out of Fulfillment. Fulfillment now
ships only physical lines; Entitlement's consumer subscribes to `OrderConfirmed` **independently** and
issues access for digital/service lines. A `DropEntitlements` / `DropUsageMetering` migration removes the
old tables. Usage keeps its overage-billing rail (`UsageOverageCharge` → Payments) unchanged.

## Consequences

- **Outbox tables are not optional.** Any DbContext wired with `AddServiceBus` (EF outbox) must register
  `AddInboxStateEntity` / `AddOutboxMessageEntity` / `AddOutboxStateEntity` in `OnModelCreating`, or the
  service crash-loops at startup. `compose-smoke` did not catch this; `kind-deploy` readiness did.
- **Consumer type names are queue names.** The kebab-case endpoint formatter derives the RabbitMQ queue
  from the consumer class name. Two services that both named a consumer `OrderConfirmedConsumer` bound the
  *same* queue and **competed** for each message instead of each receiving a copy. Entitlement's consumer
  is `EntitlementIssuingConsumer` so the two fan out correctly. Distinct consumer names across services
  are now a rule.
- **kind-deploy is capacity-bound.** A 2-core CI runner cannot hold all 13 services at once (Postgres /
  RabbitMQ saturate; 1 s health probes time out). `kind-deploy` deploys the proven 7-service baseline via
  `values-dev.yaml` to prove the chart, migrations, gateway routing and frontends; the full set
  ships via `values.yaml` / `values-prod.yaml`, every image is built by the CI docker matrix, and the
  full 13-service topology is exercised by `compose-smoke` + `browser-e2e`.
- Pricing is **additive**: the Catalog `Offer` keeps its flat price (ADR-0028) so the checkout path is
  untouched; the Pricing service is the dedicated home recurring/usage charging bills against.
