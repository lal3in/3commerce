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
