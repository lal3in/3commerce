# ADR-0034: Two-lane messaging — RabbitMQ operational bus plus Kafka durable event stream

- **Status:** Accepted
- **Date:** 2026-06-29
- **Source:** Production-grade messaging/eventing architecture plan (`.ai-shared/plans/production-grade-messaging-eventing-architecture.md`, `msg_1`) and production platform roadmap (`pplat_4`)

## Context

ADR-0007 chose MassTransit + RabbitMQ + EF transactional outbox/inbox + sagas for the operational service bus. That remains the correct backbone for commands, state-changing workflow, retries, delayed redelivery, and money-adjacent saga orchestration.

The platform now also needs a durable, append-only event-stream lane for history/replay, analytics, central audit projection, fraud/risk signals, billing/usage streams, warehouse exports, and read-model rebuilds. RabbitMQ delivery streams are optimized for operational processing and retry, not long-retention replay or late-joining analytical consumers.

## Decision

Adopt a **two-lane messaging architecture**:

1. **RabbitMQ + MassTransit remains the operational bus.**
2. **Apache Kafka is added as a separate durable event-stream lane for committed facts.**

Kafka is a complement, not a replacement. Core checkout, payment authorization, RMA/refund, fulfillment, subscription, entitlement, usage, and admin workflows must continue to succeed without Kafka being available. Kafka relay lag is acceptable; lost or duplicate financial mutations are not.

## Lane 1 — RabbitMQ / MassTransit operational bus

Use RabbitMQ/MassTransit for:

- commands that ask another service to do work, such as `AuthorizePayment`, `RefundRequested`, `SubscriptionRequested`, future capture/settlement commands, and fulfillment work items;
- saga orchestration and timeouts, including checkout and RMA/refund flows;
- retryable operational events that mutate another service's current state;
- delayed redelivery and short/medium-lived operational queues;
- request/reply only where a bounded operational response is required.

Rules:

- Keep EF transactional outbox for every database write + operational publish.
- Keep inbox/idempotency for all consumers that mutate state.
- Version message contracts additively.
- Operational messages are not the canonical replay history.
- Do not route commands through Kafka.

## Lane 2 — Kafka durable event stream

Use Kafka for:

- committed facts after the owning service has durably accepted them;
- long-retention event history;
- central audit projection and external SIEM/export feeds;
- analytics, fraud/risk, and business intelligence;
- billing/usage period-close replay;
- read-model rebuilds and late-joining consumers;
- data lake/warehouse feeds and CDC-adjacent exports.

Rules:

- Kafka events are facts, not commands.
- Kafka is not the source of truth for orders, ledger entries, subscriptions, entitlements, inventory, or service-owned state.
- Producers must publish from a committed stream outbox/relay table or equivalent committed fact source. Do not dual-write directly to Kafka in request handlers.
- Long-retention streams must be privacy-reviewed and payload-minimized.
- Consumers must be idempotent by `eventId` or a durable projection watermark. Offset commit alone is not business idempotency.

## Stream event envelope taxonomy

Every Kafka event must include these envelope fields:

| Field | Requirement |
|---|---|
| `eventId` | Globally unique UUIDv7/idempotency key. |
| `eventType` | Stable dotted or Pascal-case fact name, e.g. `OrderConfirmed`. |
| `eventVersion` | Positive integer; breaking payload changes require a new version. |
| `schemaVersion` | Envelope schema version; start at `1`. |
| `occurredAt` | UTC timestamp from the owning service's committed fact. |
| `sourceService` | Owning service name, lower-case (`ordering`, `payments`, etc.). |
| `tenantId` | Required for tenant-owned facts; nullable only for platform-global facts. |
| `aggregateId` | Aggregate/root id when the fact belongs to one. |
| `partitionKey` | Explicit key used to publish to Kafka. |
| `correlationId` | Cross-request/workflow correlation id when known. |
| `causationId` | Message/request id that caused the fact when known. |
| `traceId` | OpenTelemetry trace id when available. |
| `privacyClass` | One of `Public`, `Internal`, `Confidential`, `Restricted`. |
| `payload` | Versioned JSON payload, minimized for the topic purpose. |

### Event naming

- Facts are past tense: `OrderConfirmed`, `RefundIssued`, `InventoryMovementRecorded`, `UsageRecorded`, `AuditEntryRecorded`.
- Topic names are stable, plural, and domain-scoped: `commerce.orders`, `payments.ledger`, `audit.entries`.
- Payload field changes are additive within a version. Breaking changes use a new `eventVersion` with a dual-consumer/upcaster period.

## Initial Kafka topic catalog

| Topic | Producer | Key | Privacy ceiling | Retention target | Notes |
|---|---|---|---|---|---|
| `audit.entries` | Audit projection or local-audit relay | `tenantId` | `Confidential` | 7y+ / archive | No sensitive values; field labels/reasons only. |
| `commerce.orders` | Ordering stream relay | `tenantId:orderId` | `Internal` | 3–7y | Lifecycle facts, no card/payment secrets. |
| `payments.ledger` | Payments stream relay | `tenantId:journalEntryId` | `Internal` | 7y+ | Journal summaries/refs; ledger DB remains truth. |
| `catalog.offers` | Catalog stream relay | `tenantId:offerId` | `Internal` | 1–3y | Offer/pricing facts; supplier cost/margin excluded unless explicitly approved. |
| `usage.records` | Usage/Fulfillment stream relay | `tenantId:customerMeterId` | `Confidential` | 3–7y | Metering facts for billing replay; minimize customer identifiers. |
| `marketing.events` | Marketing collector relay | `tenantId:visitorId` | `Confidential` | configurable | Consent-gated; IP anonymized; no session replay. |
| `workflow.runs` | Workflow scheduler | `tenantId:jobName` or `jobName` | `Internal` | 1y | Job run states, failures, and misfires. |
| `webhook.deliveries` | Webhook dispatcher | `tenantId:endpointId` | `Internal` | 1y | Delivery attempts/outcomes only. |

## Privacy classification

| Class | Meaning | Kafka rule |
|---|---|---|
| `Public` | Safe for broad internal publication. | Allowed on approved topics. |
| `Internal` | Operational/business metadata without PII/secrets. | Default for most platform facts. |
| `Confidential` | PII-adjacent or tenant/customer-sensitive data. | Requires explicit topic approval, minimization, and retention review. |
| `Restricted` | Card data, bank details, secrets, tokens, password/session hashes, provider raw payloads. | Must not be published to Kafka. |

Never publish:

- PAN/CVV/raw card expiry or provider secret payloads;
- raw bank details, BSB/account numbers, IBAN/routing numbers;
- password hashes, session hashes, email-token hashes, API keys, OAuth tokens;
- raw IP addresses;
- supplier cost/margin unless topic-approved as internal restricted-access business data.

## Partition-key rules

- Aggregate lifecycle topics: `tenantId:aggregateId`.
- Tenant audit streams: `tenantId` unless aggregate ordering is required.
- Ledger streams: `tenantId:journalEntryId` for journal facts; choose `tenantId:ledgerAccountId` only for account-ordered projections.
- Usage streams: `tenantId:customerMeterId` or `tenantId:customerId:meterType`.
- Marketing streams: `tenantId:visitorId` only after consent/privacy review.

## Stream outbox and relay ownership

Each producing service owns its stream outbox table in its own database and named schema. The relay:

1. reads unpublished rows with bounded batches and `SKIP LOCKED` semantics;
2. publishes to Kafka using the stored topic/key/envelope;
3. marks rows as published only after broker acknowledgement;
4. records attempts/last error for operations;
5. never deletes rows until a retention/archive policy is implemented.

## Scheduler ownership

RabbitMQ/MassTransit remains responsible for saga timeouts and operational delayed redelivery. Quartz persistent clustered scheduling is the target for recurring jobs, retry sweeps, period-close billing, feed generation, exports, and scheduled publishing. Production-critical schedules should become Workflow-owned with persistent AdoJobStore clustering; local service-owned cron jobs may remain until migrated.

## Alternatives considered

- **Replace RabbitMQ with Kafka** — rejected. Kafka is not a command/saga broker and would regress operational workflow semantics.
- **Keep only RabbitMQ** — rejected for long-retention replay/analytics and late-joining consumers.
- **Event-source every domain aggregate in Kafka** — rejected. Service-owned databases remain source of truth; Kafka carries committed facts.
- **Direct request-handler Kafka writes** — rejected due dual-write risk. Use a committed stream outbox/relay.

## Consequences

- The next implementation slices can add package/dependency decisions, stream envelope contracts, producer/consumer abstractions, and stream outbox/relay tables against this taxonomy.
- Kafka outage must not block checkout/payment/RMA/fulfillment workflows.
- Every Kafka payload needs privacy classification and redaction tests before broad production use.
- Operations must eventually track producer failures, stream relay backlog, consumer lag, DLQs, and replay tooling.
