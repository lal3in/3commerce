# Production-grade Messaging and Eventing Architecture Plan

Last Modified Date-Time: 2026-06-28
Status: Completed
Branch when executing: `feat/production-messaging-eventing`

## Goal

Introduce a production-grade two-lane messaging architecture:

1. **RabbitMQ + MassTransit remains the operational bus** for commands, sagas, workflows, RPC-style request/reply where unavoidable, retries, delayed redelivery, and consumer inbox/outbox idempotency.
2. **Apache Kafka is added as a durable append-only event stream** for analytics, central audit projection, fraud/risk signals, billing/usage streams, historical replay, and read-model rebuilds.
3. **Quartz scheduling is hardened for production** with persistent clustered storage and clear ownership of scheduled operational work.

This plan has been implemented through tracker tasks `msg_1`–`msg_17`.

## Non-goals

- Do not replace RabbitMQ/MassTransit with Kafka.
- Do not introduce Redpanda; the target event-stream broker is Apache Kafka.
- Do not make Kafka the source of truth for orders, ledger entries, subscriptions, entitlements, or service-owned state.
- Do not bypass EF transactional outbox/inbox guarantees with direct dual writes.
- Do not allow cross-service database reads for replay/read models.
- Do not move card, bank, secret, or sensitive payloads into event streams.

## Codebase analysis

### Current implementation shape

- `src/BuildingBlocks/Infrastructure/Messaging/MassTransitExtensions.cs:15` wires DB-owning services to MassTransit with an EF transactional outbox and inbox-based consumer idempotency. This is the core operational-bus primitive and should remain unchanged in purpose.
- `src/BuildingBlocks/Infrastructure/Messaging/MassTransitExtensions.cs:60` also has a worker overload without persistence, explicitly warning consumers must tolerate redelivery.
- `docs/adr/0007-masstransit-rabbitmq-outbox-sagas.md` accepts MassTransit + RabbitMQ + EF outbox for async-first service communication and notes Kafka/event sourcing was rejected for v1 due to operational weight, not because it is inherently incompatible.
- `src/Services/Ordering/Infrastructure/Sagas/CheckoutStateMachine.cs` and related saga state show operational workflow belongs on RabbitMQ/MassTransit, not Kafka.
- `src/BuildingBlocks/Infrastructure/Audit/AuditRecorder.cs:37` records local hash-chained audit entries and optionally publishes `AuditEntryRecorded` through the MassTransit outbox.
- `src/BuildingBlocks/Infrastructure/Scheduling/JobExecutor.cs:37` records local job runs and optionally publishes `JobRunRecorded` through MassTransit.
- `src/BuildingBlocks/Infrastructure/Scheduling/SchedulingExtensions.cs:44` wires Quartz recurring jobs but currently as service-local scheduling primitives, not yet a fully persistent clustered scheduler service.
- `src/BuildingBlocks/Infrastructure/Observability/OtelExtensions.cs:18` wires OpenTelemetry traces/metrics; Kafka/Rabbit bridges should extend this with producer/consumer traces and broker metrics.
- `docker-compose.yml:36` defines RabbitMQ. There is no Kafka cluster, Schema Registry, Kafka UI, Connect, or stream-relay worker today.
- `docker-compose.yml:189` defines the opt-in observability stack. Kafka/Rabbit dashboards should join this posture.
- `deploy/helm/3commerce/templates/infra.yaml:92` deploys RabbitMQ as a single replica in the chart today. Kafka should be added with production values designed for managed Kafka first and local/dev Kafka second.
- `deploy/helm/3commerce/templates/services.yaml:10`, `apps.yaml:10`, and `gateway.yaml:9` default app replicas to one. The messaging plan must explicitly handle scale-out idempotency, consumer groups, partition keys, and scheduler clustering before raising replicas.
- ADRs `0029` and `0030` establish capability-first ops/compliance primitives and extracted Audit/Workflow services. These are the natural first consumers/producers for Kafka stream projection.

### Key gap

The system has a strong operational bus, but it lacks a durable event-stream lane for cross-cutting history/replay. Current MassTransit events are optimized for workflow delivery and retries, not long-retention stream replay, analytics fan-out, or late-joining consumers.

## Documentation references

- ADR-0007: MassTransit + RabbitMQ + EF outbox/sagas, including why Kafka was deferred in v1.
- ADR-0011: YARP single-origin gateway. Kafka does not alter public ingress.
- ADR-0022: named schema per service; any relay tables must use named schemas and `public.__EFMigrationsHistory`.
- ADR-0024: tenant isolation with PostgreSQL RLS; stream relay jobs must preserve tenant metadata and never leak cross-tenant payloads.
- ADR-0029: Phase 6 compliance/ops primitives, including audit, workflow, scheduling, notifications, webhooks, exports, storage, and observability.
- ADR-0030: extracted service rule set for Audit/Workflow/etc.
- Apache Kafka docs: topics are partitioned append-only logs; consumer groups divide partitions among consumers.
- Confluent .NET Kafka client docs: use `Confluent.Kafka` for producer, consumer, and admin clients against Apache Kafka brokers.
- Quartz.NET docs: `AdoJobStore` persists jobs/triggers/calendars in a relational database and supports clustering.
- YARP docs: load balancing supports power-of-two choices, least requests, round-robin, random, first, and destination health; this is separate from Kafka/Rabbit internals.

## Target architecture

### Lane 1: RabbitMQ / MassTransit operational bus

Use for:

- Commands: `AuthorizePayment`, future `CapturePayment`, `IssueRefund`, `ProvisionEntitlement`, etc.
- Sagas/state machines: checkout, RMA/refund, approval/workflow orchestration.
- Retryable operational events that mutate service state.
- Delayed redelivery, scheduled retry, and workflow fan-out.
- Request/reply where a bounded operational response is required.

Rules:

- Keep EF transactional outbox for all DB write + publish operations.
- Keep inbox/idempotency for all consumers.
- Keep message contracts versioned additively.
- Operational messages may be compact and command-like; they are not the canonical replay history.

### Lane 2: Apache Kafka durable event-stream lane

Use for:

- Append-only analytical/event history.
- Central audit projection and tamper-evidence anchoring.
- Fraud/risk model inputs.
- Billing/usage streams and period-close replays.
- Read-model rebuilds and late-joining consumers.
- Data lake / warehouse / CDC-adjacent export feeds.

Rules:

- Kafka stream events are facts already committed by an owning service.
- Kafka producers must publish from a committed outbox/relay table or from MassTransit outbox-produced facts, never by dual-writing within request handlers.
- Events must include `eventId`, `eventType`, `eventVersion`, `occurredAt`, `tenantId`, `sourceService`, `correlationId`, `causationId`, `schemaVersion`, and privacy classification.
- Partition keys must preserve ordering where it matters: `tenantId:aggregateId` for aggregate facts; `tenantId` for tenant-wide audit streams; `customerId`/email-hash only when privacy reviewed.
- Long retention is intentional; payloads must be minimised and redacted by design.

### Scheduler

Use Quartz persistent clustered scheduling for:

- Recurring operational jobs that must survive restarts.
- Retry sweeps for webhooks/exports/outbound jobs where not handled by Rabbit delayed redelivery.
- Period-close jobs for usage/subscription billing.
- Scheduled publishing/feed generation.

Rules:

- Typed code jobs only; no admin-defined SQL/shell.
- Persist job definitions/triggers in Workflow-owned schema/db where the job is centrally owned.
- Local per-service jobs may remain for simple service-owned cron, but production critical schedules should migrate to Workflow with AdoJobStore clustering.

## Proposed Kafka topic taxonomy

Initial topics, all prefixed by environment in non-prod if needed (`dev.audit.entries`, etc.):

| Topic | Producer | Consumers | Key | Retention | Notes |
|---|---|---|---|---|---|
| `audit.entries` | audit relay from local audit/MassTransit events | Audit service, SIEM/export | `tenantId` | 7y+ or compacted anchor + archive | No sensitive values; field labels/reasons only |
| `commerce.orders` | Ordering relay | analytics, fraud, read models | `tenantId:orderId` | 3-7y | Order lifecycle facts, not card/payment data |
| `payments.ledger` | Payments relay | accounting analytics, reconciliation | `tenantId:journalEntryId` | 7y+ | Journal summaries/refs, no provider secrets |
| `catalog.offers` | Catalog relay | search/read models, feeds | `tenantId:offerId` | 1-3y | Offer/pricing facts; no supplier-cost unless explicitly classified internal-only |
| `usage.records` | Usage/Fulfillment relay | billing, analytics | `tenantId:customerMeterId` | 3-7y | Append-only meter events |
| `marketing.events` | Marketing collector relay | analytics | `tenantId:visitorId` | configurable short/medium | Consent-gated, IP anonymized |
| `workflow.runs` | Workflow scheduler | ops dashboards | `tenantId:jobName` or `jobName` | 1y | Job run facts and state transitions |
| `webhook.deliveries` | Webhook dispatcher | ops/audit | `tenantId:endpointId` | 1y | Delivery attempts/outcomes only |

## Ordered implementation tasks

| Task_ID | Task_Name | Phase | Status | Plan Path | Comments |
|---|---|---|---|---|---|
| msg_1 | ADR and contract taxonomy | P1 architecture | pending | `.ai-shared/plans/production-grade-messaging-eventing-architecture.md` | Write ADR-0031 defining RabbitMQ vs Kafka responsibilities, topic taxonomy, privacy rules, retention, partition-key rules, and scheduler ownership. Update ADR index. |
| msg_2 | Kafka package/dependency decision | P1 architecture | pending | `.ai-shared/plans/production-grade-messaging-eventing-architecture.md` | Add `Confluent.Kafka` using install command; evaluate Schema Registry package only if Avro/Protobuf is selected. Do not manually edit package versions except CPM normalization after install. |
| msg_3 | Stream event envelope contracts | P1 contracts | pending | `.ai-shared/plans/production-grade-messaging-eventing-architecture.md` | Add shared stream envelope primitives under BuildingBlocks.Infrastructure or Contracts without domain logic; include event metadata, privacy class, tenant id, trace ids, schema version, and idempotency key. |
| msg_4 | Kafka producer abstraction | P1 infrastructure | pending | `.ai-shared/plans/production-grade-messaging-eventing-architecture.md` | Add typed producer seam, topic resolver, JSON serialization v1, headers, OpenTelemetry activity, metrics, retry/error handling, and test fake. |
| msg_5 | Kafka consumer abstraction | P1 infrastructure | pending | `.ai-shared/plans/production-grade-messaging-eventing-architecture.md` | Add hosted consumer base with consumer groups, manual offset commit after successful processing, poison handling, dead-letter topic strategy, idempotent handler guidance, and graceful shutdown. |
| msg_6 | Stream outbox/relay model | P2 reliability | pending | `.ai-shared/plans/production-grade-messaging-eventing-architecture.md` | Add per-service `StreamOutboxMessages` table pattern in named schema; relay publishes committed rows to Kafka and marks published. Avoid request-handler dual writes. |
| msg_7 | Bridge existing audit facts to stream outbox | P2 audit | pending | `.ai-shared/plans/production-grade-messaging-eventing-architecture.md` | Extend local audit recorder or central Audit projection to stage stream facts. Validate no sensitive values are serialized. |
| msg_8 | Bridge order/payment/catalog/usage facts | P2 domain streams | pending | `.ai-shared/plans/production-grade-messaging-eventing-architecture.md` | Add minimal stream facts from owning services only. Start with order confirmed/cancelled, payment ledger journal posted, offer changed, usage recorded. |
| msg_9 | Kafka local/dev stack | P3 deploy | pending | `.ai-shared/plans/production-grade-messaging-eventing-architecture.md` | Add Apache Kafka dev services to compose, using KRaft or image-supported local single-node mode. Add optional Kafka UI only for dev profile. RabbitMQ stays. |
| msg_10 | Kafka Helm support | P3 deploy | pending | `.ai-shared/plans/production-grade-messaging-eventing-architecture.md` | Prefer managed Kafka in prod via external bootstrap servers/secrets. Provide dev chart values for internal Kafka only if needed. Include network policies and TLS/SASL placeholders. |
| msg_11 | Persistent clustered Quartz scheduler | P4 scheduler | pending | `.ai-shared/plans/production-grade-messaging-eventing-architecture.md` | Migrate production-critical scheduling to Quartz AdoJobStore with Workflow-owned tables, clustering enabled, misfire policy, and job-run projection. |
| msg_12 | Message scheduling policy | P4 scheduler | pending | `.ai-shared/plans/production-grade-messaging-eventing-architecture.md` | Decide Rabbit delayed/scheduled messages vs Quartz jobs per use case. Document and test saga timeouts, recurring jobs, retry sweeps, and billing period close. |
| msg_13 | Observability and operations dashboards | P5 ops | pending | `.ai-shared/plans/production-grade-messaging-eventing-architecture.md` | Add broker metrics, producer/consumer lag, failed relay rows, DLQ counts, Rabbit queue depth, Kafka consumer lag, scheduler misfires, and dashboards/alerts. |
| msg_14 | Security/privacy hardening | P5 ops | pending | `.ai-shared/plans/production-grade-messaging-eventing-architecture.md` | Enforce topic ACL plan, TLS/SASL config, secret handling, privacy classifications, payload redaction tests, and tenant metadata checks. |
| msg_15 | Replay/read-model consumer proof | P6 validation | pending | `.ai-shared/plans/production-grade-messaging-eventing-architecture.md` | Build a non-critical replay consumer that rebuilds a read model from Kafka without touching other services' DBs. Prove idempotency and offset recovery. |
| msg_16 | Production runbooks and API/docs updates | P6 docs | pending | `.ai-shared/plans/production-grade-messaging-eventing-architecture.md` | Update deployment/help docs, API contracts index for event docs, e2e-verify checklist, topic catalog, operational runbooks, and project structure if files/services are added. |
| msg_17 | End-to-end resilience validation | P7 validation | pending | `.ai-shared/plans/production-grade-messaging-eventing-architecture.md` | Test broker outage/recovery, relay restart, duplicate publish, consumer replay, poison handling, schema evolution, scheduler failover, and no-RLS/tenant leakage. |

## Implementation details by area

### Stream outbox pattern

Recommended table shape per producing service:

- `Id` UUIDv7 primary key.
- `Topic` string.
- `Key` string.
- `EventType` string.
- `EventVersion` int.
- `TenantId` UUID nullable only for platform-global facts.
- `PayloadJson` JSONB.
- `HeadersJson` JSONB.
- `OccurredAt` timestamp.
- `AvailableAt` timestamp for delayed export if needed.
- `PublishedAt` timestamp nullable.
- `PublishAttempts` int.
- `LastError` string nullable.
- Indexes: `(PublishedAt, AvailableAt) WHERE PublishedAt IS NULL`, `(Topic, Key)`, `(TenantId, OccurredAt)`.

Relay behavior:

1. Poll unpublished rows in small batches with SKIP LOCKED.
2. Publish to Kafka with deterministic key and event id header.
3. On ack, mark `PublishedAt`.
4. On error, increment attempts and back off.
5. Never delete until retention/archive policy is implemented.

### Schema strategy

Start with versioned JSON envelopes for speed and readability. Add Schema Registry later if event volume and multi-language consumers justify it.

Minimum compatibility rule:

- Only additive fields in an existing event version.
- Breaking changes require a new `eventVersion` and upcaster/dual-consumer period.
- Unknown fields must be ignored by consumers.

### Privacy rules

- `Public`: safe for broad internal analytics.
- `Internal`: operational metadata; no PII/secrets.
- `Confidential`: PII-adjacent; explicit topic approval required.
- `Restricted`: card/bank/provider secrets/raw identifiers; must not be published to Kafka.

Payload rules:

- No card PAN/CVV/expiry beyond existing safe brand/last4 display rules.
- No raw bank details; token refs/masked values only if required.
- No password/session/token hashes.
- No raw IP; use existing anonymizer patterns.
- No supplier cost/margin unless on an explicitly internal restricted-access topic with business approval.

### Partitioning rules

- Aggregate streams: key `tenantId:aggregateId`.
- Tenant audit streams: key `tenantId`.
- Ledger streams: key `tenantId:journalEntryId` or `tenantId:ledgerAccountId` depending on required ordering.
- Usage streams: key `tenantId:customerId:meterType` for period aggregation ordering.
- Marketing events: key `tenantId:visitorId` only after consent/privacy review.

### Consumer idempotency

Every stream consumer must store processed `eventId` or a projection watermark. Offset commit alone is not sufficient for business idempotency.

### Dead-letter strategy

- Serialization/schema failure: write to `*.dlq` with original headers and error class.
- Transient dependencies: retry with bounded backoff; do not commit offset until success or DLQ handoff.
- Poison events: DLQ + alert + operator replay tool.

### Scheduler hardening

- Use Quartz `AdoJobStore` for persistent triggers/calendars.
- Enable clustering for multi-replica Workflow service.
- Store Quartz tables in Workflow DB/schema, with migration ownership documented.
- Define misfire policy per job type.
- Ensure jobs are idempotent by `(jobName, scheduledFireTime)` or a domain-specific reference id.
- Continue to publish `JobRunRecorded` and optionally stream `workflow.runs` to Kafka.

## Validation commands

Run targeted validation as each slice lands:

```bash
# C# baseline
dotnet build 3commerce.sln
dotnet format --verify-no-changes
dotnet test 3commerce.sln

# Integration slices, Docker/colima required
dotnet test tests/3commerce.IntegrationTests/3commerce.IntegrationTests.csproj --filter "Category=Integration"

# Messaging-specific filters to add while implementing
dotnet test tests/3commerce.IntegrationTests/3commerce.IntegrationTests.csproj --filter "Kafka|StreamOutbox|Scheduler|Replay"

# Compose/Helm config validation
docker compose config
docker compose --profile observability config
helm template 3commerce deploy/helm/3commerce -f deploy/helm/3commerce/values-dev.yaml >/tmp/3commerce-rendered.yaml
helm template 3commerce deploy/helm/3commerce -f deploy/helm/3commerce/values-prod.yaml >/tmp/3commerce-prod-rendered.yaml

# Storefront/admin only if touched
cd src/Storefront && npm run lint && npx tsc --noEmit
```

Full regression before marking the plan complete:

```bash
scripts/e2e-verify.sh
# If stack-level broker/scheduler behavior changes are live-testable:
scripts/e2e-verify.sh --live
```

## Acceptance criteria

- RabbitMQ/MassTransit operational workflows still pass all existing saga, checkout, RMA, payment, fulfillment, and subscription tests.
- Kafka is present as a separate append-only stream lane and is not required for core checkout success.
- At least one producing service stages committed stream facts through a stream outbox and relay.
- At least one replay consumer rebuilds a read model idempotently from Kafka.
- Producer and consumer traces/metrics appear in OTel/Prometheus/Grafana.
- Consumer lag, relay failures, DLQ counts, and scheduler misfires are alertable.
- Persistent clustered Quartz survives process restart and does not double-run idempotent jobs under two Workflow replicas.
- Topic catalog, privacy classification, partition-key rules, retention, and schema evolution rules are documented.
- No restricted payloads appear in stream topics; automated tests assert redaction on representative events.
- Compose and Helm can configure RabbitMQ and Kafka independently.

## Risks and mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| Dual-write bugs between DB and Kafka | Lost or phantom stream facts | Use committed stream outbox + relay only |
| Kafka used for operational commands | Saga reliability regression | ADR boundary: RabbitMQ/MassTransit owns commands/sagas |
| PII/secrets in long-retention streams | Compliance/security incident | Privacy classification, redaction helpers, tests, topic review |
| Partition-key mistakes | Reordered aggregate history | Document keys and test ordering-sensitive consumers |
| Consumer offset commit before durable projection | Data loss on crash | Manual commit after idempotent projection commit |
| Scheduler double-runs under scale-out | Duplicate billing/jobs | Quartz clustering + job-level idempotency references |
| Operational load from self-hosted Kafka | Production fragility | Prefer managed Kafka in prod; local single-node only for dev |
| Schema drift | Broken consumers | Additive contracts, eventVersion, upcasters, compatibility tests |
| More infra overwhelms local dev | Slower workflows | Kafka optional profile until stream tasks are under test |

## Rollout strategy

1. Document ADR and boundaries first.
2. Add Kafka infrastructure disabled/optional for local development.
3. Add stream envelope and producer/consumer abstractions with fakes and unit tests.
4. Add stream outbox/relay to one low-risk domain fact.
5. Expand to audit/order/payment/usage facts.
6. Add replay consumer proof.
7. Harden scheduler persistence and Workflow ownership.
8. Add dashboards, alerts, runbooks, and live resilience tests.
9. Only then consider making Kafka mandatory in non-dev deployments.

## Confidence score

**0.82**

Reasoning: The current codebase already has strong MassTransit outbox/inbox, audit, scheduling, and observability primitives, so Kafka can be added as a complementary relay/replay lane without rewriting core workflows. Main uncertainty is operational scope: production Kafka, Schema Registry, ACLs, and clustered Quartz require careful deploy and CI resources beyond the current single-replica dev chart posture.
