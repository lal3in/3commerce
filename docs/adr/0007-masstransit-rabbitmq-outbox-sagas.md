# ADR-0007: MassTransit + RabbitMQ, async-first, EF outbox, saga orchestration

- **Status:** Accepted
- **Date:** 2026-06-12
- **Source:** PRD design interview (decision log #7, `docs/prd/3commerce/15-appendix.md`)

## Context

Multi-service flows (checkout: order → payment → fulfillment) need coordination. Synchronous call chains create a distributed monolith; dual writes (DB + broker) without an outbox lose messages.

## Decision

- Events between services via **RabbitMQ** with **MassTransit v8**.
- **Async-first:** state changes propagate as events; sync REST between services only for read-time queries, never inside a saga step.
- **EF Core transactional outbox** for every "write DB + publish event".
- **Saga state machines** (MassTransit) for money-adjacent flows: checkout (in Ordering), refund (Support → Payments), with timeouts and compensation.
- **Idempotent consumers** everywhere (dedup by message ID).

## Alternatives considered

- **Kafka + event sourcing** — rejected for v1: operationally heavy; event-sourcing the whole domain first time usually ends in rewrite. Possible later refit for the ledger only.
- **Synchronous REST/gRPC chains** — rejected: distributed transactions with no tooling.
- **Dapr** — rejected: abstracts away the exact patterns the project exists to learn.

## Consequences

- Message contracts live in `BuildingBlocks.Contracts`, versioned additively only.
- Chaos test required: killing a service mid-saga must recover to a correct terminal state (NFR-2).

## Amendment 2026-10-08 — consumers that race on one key

The EF outbox runs every consumer in a REPEATABLE READ transaction whose snapshot is taken at the inbox
lock, before the consumer's own reads. Two messages that write the same row therefore collide (`23505` on
an insert, `40001` on an update or an `ON CONFLICT` clause) whenever they are consumed in parallel, and the
inbox does not help unless they share a message id. Retries restore the end state, but each collision costs
a retry and an error log, and under load a message can exhaust its retries and fault to `_error`. Rules:

- **Different writers of one row** get a row (or table) each, with a view for readers (ADR-0043, PR #287).
- **One writer, several messages per key** (e.g. two `OrderConfirmed` for one order): partition the
  consumer's endpoint by the key with the endpoint-level `UsePartitioner<T>(IPartitioner, key)`, added through
  `.Endpoint(e => e.AddConfigureEndpointCallback(…))` so the endpoint name stays derived (ADR-0060). It runs
  before the outbox transaction opens, so the messages of one key run one at a time while other keys stay
  parallel (Support `OrderSnapshotConsumer.PartitionByOrder`).
- **Ordered events of one signal** where the last value must win: `ConcurrentMessageLimit = 1` (or a
  partitioner) so they apply in queue order.

The partitioner is per process. Replicas that compete for one queue can still collide on a key; the retry
policy covers that case with the same end state.
