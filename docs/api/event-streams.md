# Event Stream Contracts

Kafka event-stream contracts for the durable replay/analytics lane are governed by ADR-0034.
RabbitMQ/MassTransit remains the operational command/saga bus; Kafka carries committed facts only.

Related operational docs:

- [Message scheduling policy](./message-scheduling.md)
- [Messaging observability runbook](../runbooks/messaging-observability.md)
- [Messaging security/privacy runbook](../runbooks/messaging-security.md)

## Envelope v1

All Kafka events use a versioned JSON envelope:

| Field | Type | Required | Notes |
|---|---|---:|---|
| `eventId` | UUID/string | yes | Globally unique event id and business idempotency key. |
| `eventType` | string | yes | Past-tense fact name, e.g. `OrderConfirmed`. |
| `eventVersion` | integer | yes | Breaking payload changes require a new version. |
| `schemaVersion` | integer | yes | Envelope version; starts at `1`. |
| `occurredAt` | ISO-8601 UTC timestamp | yes | When the owning service committed the fact. |
| `sourceService` | string | yes | Lower-case service name. |
| `tenantId` | UUID/string/null | yes | Null only for platform-global facts. |
| `aggregateId` | UUID/string/null | no | Aggregate/root id when applicable. |
| `partitionKey` | string | yes | Exact key used for Kafka publication. |
| `correlationId` | string/null | no | Workflow/request correlation id when known. |
| `causationId` | string/null | no | Request/message id that caused this fact when known. |
| `traceId` | string/null | no | OpenTelemetry trace id when available. |
| `privacyClass` | enum | yes | `Public`, `Internal`, `Confidential`, or `Restricted`. |
| `payload` | object | yes | Versioned, minimized payload for the topic. |

## Topic catalog

| Topic | Producer | Partition key | Privacy ceiling | Purpose |
|---|---|---|---|---|
| `audit.entries` | Audit projection or local-audit relay | `tenantId` | `Confidential` | Audit search/export; no sensitive values. |
| `commerce.orders` | Ordering stream relay | `tenantId:orderId` | `Internal` | Order lifecycle replay/analytics. |
| `payments.ledger` | Payments stream relay | `tenantId:journalEntryId` | `Internal` | Ledger journal facts and reconciliation analytics. |
| `catalog.offers` | Catalog stream relay | `tenantId:offerId` | `Internal` | Offer/pricing/feed projections. |
| `usage.records` | Usage/Fulfillment stream relay | `tenantId:customerMeterId` | `Confidential` | Metering replay and period-close billing. |
| `marketing.events` | Marketing collector relay | `tenantId:visitorId` | `Confidential` | Consent-gated behavioral analytics. |
| `workflow.runs` | Workflow scheduler | `tenantId:jobName` or `jobName` | `Internal` | Job run/misfire dashboards. |
| `webhook.deliveries` | Webhook dispatcher | `tenantId:endpointId` | `Internal` | Outbound webhook delivery operations. |

## Compatibility rules

- Existing event versions may add optional fields only.
- Breaking payload changes require a new `eventVersion`.
- Consumers must ignore unknown fields.
- Consumers must store `eventId` or a durable projection watermark; Kafka offset commits alone are not business idempotency.
- Replay/read-model rebuilds use the same idempotent consumer path plus a durable watermark (`topic`, `consumerGroup`, `offset`) before committing offsets.
- `Restricted` payloads must not be published to Kafka; producer/outbox guardrails reject them before publish/staging.
- Tenant-scoped topics require `tenantId`; platform-global exceptions must be explicit.

## Example

```json
{
  "eventId": "018f65b8-6f57-7b7a-9a29-4fb73f01c5df",
  "eventType": "OrderConfirmed",
  "eventVersion": 1,
  "schemaVersion": 1,
  "occurredAt": "2026-06-29T02:30:00Z",
  "sourceService": "ordering",
  "tenantId": "00000000-0000-0000-0000-000000000001",
  "aggregateId": "018f65b8-7000-7e1a-b8df-6516e3e38d10",
  "partitionKey": "00000000-0000-0000-0000-000000000001:018f65b8-7000-7e1a-b8df-6516e3e38d10",
  "correlationId": "checkout-018f65b8",
  "causationId": "payment-succeeded-018f65b8",
  "traceId": "4bf92f3577b34da6a3ce929d0e0e4736",
  "privacyClass": "Internal",
  "payload": {
    "orderId": "018f65b8-7000-7e1a-b8df-6516e3e38d10",
    "status": "Confirmed",
    "currency": "EUR",
    "grossMinor": 31799
  }
}
```
