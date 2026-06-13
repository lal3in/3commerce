# 6. Core Architecture & Patterns

## High-level approach

Six C# microservices cut along **business capability seams** (not entities), communicating **async-first** over RabbitMQ via MassTransit, each owning its own PostgreSQL database. A YARP gateway is the single public origin. Microservices were chosen explicitly for distributed-systems learning value, with the acknowledged cost of solo-dev complexity; the compensating rule is *simple inside each service*.

```
                ┌────────────────────┐   ┌─────────────────────┐
                │ Next.js storefront │   │ Blazor Server admin │
                │       (SSR)        │   │ (subdomain + IP ACL)│
                └─────────┬──────────┘   └──────────┬──────────┘
                          │  HTTPS (cookie auth)    │
                       ┌──▼─────────────────────────▼──┐
                       │   YARP Gateway (C#)           │
                       │ session validation, rate limit│
                       │ → internal signed claims      │
                       └──┬────┬────┬────┬────┬────┬───┘
                          │    │    │    │    │    │   REST (sparse, queries only)
   ┌──────────┐ ┌─────────▼┐ ┌─▼──────┐ ┌▼───────┐ ┌▼──────────┐ ┌▼────────┐
   │ Identity │ │ Catalog  │ │Ordering│ │Payments│ │Fulfillment│ │ Support │
   │  (auth)  │ │(products,│ │ (cart, │ │(ledger,│ │ (supplier │ │(tickets,│
   │          │ │ search,  │ │checkout│ │ Stripe,│ │ orders,   │ │  RMA)   │
   │          │ │importers)│ │  saga) │ │  Xero) │ │ tracking) │ │         │
   └────┬─────┘ └────┬─────┘ └───┬────┘ └───┬────┘ └─────┬─────┘ └────┬────┘
        │            │           │          │            │            │
        └────────────┴───────────┴────┬─────┴────────────┴────────────┘
                                      │ events (MassTransit)
                              ┌───────▼────────┐      ┌──────────────────┐
                              │   RabbitMQ     │      │ PostgreSQL (one  │
                              └────────────────┘      │ container, one DB│
                                                      │ per service)     │
                                                      └──────────────────┘
```

## Service responsibilities

| Service | Owns | Key flows |
|---|---|---|
| **Identity** | users, credentials (Argon2id), sessions, roles, email verification/reset tokens | login, session introspection for gateway, guest→account conversion |
| **Catalog** | products, variants, categories, JSONB attributes, import runs, search index | `ISupplierImporter` ingestion, FTS search, browse |
| **Ordering** | carts (anonymous + user), orders, line items **with per-line fulfillment source**, checkout saga state | cart ops, checkout saga orchestration, order history projection |
| **Payments** | double-entry ledger (source of truth), payment intents, refunds, Stripe webhook inbox, Xero sync state | authorize/capture/refund, webhook reconciliation, nightly Xero journals |
| **Fulfillment** | shipments, supplier order forwarding, tracking numbers | reacts to `OrderConfirmed`, emits `ShipmentDispatched`/`TrackingAssigned` |
| **Support** | tickets, RMA state machine | RMA: requested → approved/denied → (return shipped) → refund issued (kicks refund saga) |

Notifications/email is a **shared worker** consuming events, not a seventh service.

## Repository layout

```
3commerce/
├── docs/prd/                      # this PRD
├── docker-compose.infra.yml       # Postgres + RabbitMQ only
├── src/
│   ├── BuildingBlocks/            # shared: messaging contracts, outbox setup,
│   │   ├── Contracts/             #   OTel wiring, internal-claims auth handler
│   │   └── Infrastructure/
│   ├── Gateway/                   # YARP
│   ├── Services/
│   │   ├── Identity/   ├── Catalog/   ├── Ordering/
│   │   ├── Payments/   ├── Fulfillment/ └── Support/
│   │   # each: Api/ Domain/ Infrastructure/ + tests/
│   ├── Workers/Notifications/
│   ├── Storefront/                # Next.js
│   └── Admin/                     # Blazor Server
└── tests/                         # cross-service integration (Testcontainers)
```

Shared code is limited to **message contracts and infrastructure plumbing** — never domain logic. Domain duplication between services is accepted over coupling.

## Key patterns and rules

1. **Async-first events; sparse sync queries.** State changes propagate via events. Synchronous REST/gRPC between services is allowed only for read-time queries that cannot be projection-fed, and never inside a saga step.
2. **Transactional outbox everywhere.** Every "write DB + publish event" uses MassTransit's EF Core outbox — no dual-write bugs by construction.
3. **Sagas for money-adjacent flows.** Checkout and refund are MassTransit state machines (orchestration, living in Ordering and Support→Payments respectively) with explicit compensation steps and timeouts.
4. **Idempotent consumers.** Every consumer tolerates redelivery (inbox/dedup by message ID). Stripe webhooks are deduplicated by event ID.
5. **Database-per-service, hard isolation.** One Postgres container, six databases, one DbContext + migration history per service. **No cross-database joins.** Cross-service display data (e.g. product names on orders) is copied locally via events at write time.
6. **Edge-stateful, internally-stateless auth.** Gateway validates the opaque cookie against Identity (with short-lived cache), then forwards a short-lived signed JWT of claims; services verify the signature only. See [09-security-configuration.md](./09-security-configuration.md).
7. **Interfaces at the four known change points only:** `ISupplierImporter`, `IPaymentProvider`, `ITaxStrategy`, `ISearchProvider` — one implementation each in v1.
8. **Ledger invariant.** Every ledger transaction balances (Σ debits = Σ credits) — enforced by a database constraint and property-based tests.
9. **Observability by default.** OpenTelemetry traces propagate via gateway headers and MassTransit message headers; one checkout = one trace.

## Local development

- Services run bare (`dotnet run` × 6 + gateway + worker); only Postgres and RabbitMQ run in containers (`docker-compose.infra.yml`).
- Kubernetes is deliberately deferred to a post-MVP deployment-learning phase; Dockerfiles are maintained per service so that step is cheap.
