# ADR-0035: Logical replication / CDC boundaries

Status: Accepted
Date: 2026-06-28
Area: Data / integration / operations

## Context

The platform now has two explicit messaging lanes:

- RabbitMQ/MassTransit for operational commands, sagas, retries, delayed redelivery, and workflow state changes.
- Kafka for committed domain facts, replay, audit projection, analytics, and data-lake style export.

PostgreSQL logical replication and CDC are still useful, but they are table-level infrastructure tools. If used as an integration surface between services, they would bypass API/event contracts, expose private schemas, undermine service ownership, and risk leaking PII or tenant-isolated data.

## Decision

Logical replication / CDC is allowed only for explicitly owned infrastructure/data-movement use cases. It is not a service-integration API.

Use service-owned Kafka stream facts for domain history and replay. Use CDC/logical replication only when a table-level feed is explicitly justified for one of these purposes:

1. Disaster-recovery support or physical/logical migration between managed Postgres instances.
2. One-time or bounded analytics bootstrap where stream history does not yet cover the required time range.
3. Operational database maintenance that requires a temporary replica during a controlled migration.
4. Warehouse export of approved, redacted, owner-reviewed tables when no domain stream exists yet and a deprecation path to stream facts is recorded.

Raw service-owned tables must never be consumed by another service for online behavior.

## Rules

- Every CDC feed needs an owner, consumer, purpose, data classification, retention, and removal/expiry plan.
- Publications must be service-owned and schema/table allow-listed. No database-wide publication by default.
- Tenant-owned tables require tenant metadata in every emitted/consumed row and must preserve RLS/privacy expectations downstream.
- Sensitive fields require redaction, hashing, or exclusion before long-retention export.
- WAL retention and replication slots must be monitored before any long-lived slot is enabled.
- Logical replication lag must be alertable; a stuck slot is a production incident because it can retain WAL indefinitely.
- CDC consumers must not write back into service-owned databases except through the owning service's API/command path.
- CDC cannot replace the stream outbox for committed domain facts.

## Operational requirements before enabling long-lived CDC

- Per-slot lag and retained-WAL dashboards.
- Alert on inactive slot, high replay lag, and high retained WAL bytes.
- Runbook for pausing/removing a slot safely.
- Privacy review for every table/column in the publication.
- Backfill/replay plan that does not require disabling RLS or querying across service databases.

## Consequences

Positive:

- Preserves service ownership and event-contract boundaries.
- Allows pragmatic data movement/migration without turning tables into public APIs.
- Makes WAL/slot operational risk explicit before production use.

Negative:

- Some analytics bootstrap work may require additional review or stream fact creation first.
- CDC is slower to adopt because it needs owner/consumer/retention/privacy evidence.

## Validation

Design/config validation for now:

```bash
docker compose config
helm template 3commerce deploy/helm/3commerce -f deploy/helm/3commerce/values-prod.yaml >/tmp/3commerce-prod-rendered.yaml
```

No CDC publications or replication slots are enabled by this ADR.
