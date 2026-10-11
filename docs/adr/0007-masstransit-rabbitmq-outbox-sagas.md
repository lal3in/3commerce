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

## Amendment 2026-10-11 — shared counters and writes from outside the partition

The 2026-10-08 rules cover consumers that race on one key. Three Ordering writes did not fit them
(order_number_and_edit_races_2026_10_11; owner's decisions of 2026-10-11):

- **A counter shared by many keys is allocated outside the consumer's snapshot.** Every order of a
  storefront takes its `PublicOrderNumber` from one `OrderNumberSequences` row, but the order-status
  endpoint is partitioned by ORDER id, so two confirmations of one storefront ran in parallel and the
  read-then-increment in the outbox's REPEATABLE READ transaction failed with `40001` (or `23505` on the
  storefront's first order). Partitioning by storefront would serialize a store's whole order flow, and no
  statement fixes it inside the snapshot (`ON CONFLICT DO UPDATE` raises `40001` there too). The number is
  now taken by `OrderNumberAllocator`: one `INSERT … ON CONFLICT ("StorefrontId") DO UPDATE SET
  "NextNumber" = "NextNumber" + 1 RETURNING "NextNumber" - 1`, in its own short READ COMMITTED transaction
  on its own connection (own Npgsql pool, via a distinct `Application Name`, so consumers holding the main
  pool cannot starve it). Concurrent allocations queue on the row lock for microseconds and never fail.
  **Order numbers may therefore have gaps**: a confirmation that rolls back burns its number. That is
  accepted — the number is an order REFERENCE, not a tax-invoice number (tax documents get their own
  gapless series, allocated with the document — ADR-0063 §4.2/§4.4). The human-facing format is unchanged (first number 1000 per storefront) and uniqueness
  per storefront stays a DB constraint (`IX_Orders_StorefrontId_PublicOrderNumber`). A per-storefront
  Postgres `SEQUENCE` was rejected: it needs runtime DDL (racy `CREATE SEQUENCE IF NOT EXISTS`, CREATE
  privilege for the service role, orphaned objects per storefront) for the same gap semantics. Ordering's
  tables have no row-level security, so the separate connection needs no tenant scope; a table that gets
  RLS later must `set_config('app.tenant_id', …)` on that connection.
- **An HTTP write to a row that consumers also write uses an optimistic-concurrency token.** The
  supplier/operator `mark-delivered` endpoint updates `ordering."Orders"` outside the order's partition.
  `Order.Version` maps Postgres's `xmin` system column as the EF concurrency token (no schema change; the
  migration emits no DDL), so the endpoint's `UPDATE … AND xmin = @loaded` matches nothing when the row
  changed after its read, and a client may also send the version it displayed. Either way the answer is
  409 `application/problem+json` (`orderRefusal` 4, "This order was just updated — refresh and try
  again."), never a lost update. On the consumer side, an HTTP commit inside a consumer's snapshot makes
  the consumer's update fail with `40001` and retry on a fresh snapshot, which is the designed outcome.
  Admin cancel needs no token: it never writes the row, it publishes `OrderCancelled`.
- **A sweep keyed by something else fans out per key.** The FR-7 guest attach (`EmailVerified`, keyed by
  email) updated every matching guest order itself, concurrently with those orders' status events. It now
  records the verified email and publishes one `GuestOrderOwnerVerified(OrderId, UserId)` per matching
  order through the outbox; `OrderStatusConsumer` consumes it on the same endpoint, through the SAME
  `IPartitioner` instance as the order's other events, so nothing writes one order row concurrently.
