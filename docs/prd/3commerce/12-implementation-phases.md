# 12. Implementation Phases

Solo-dev estimates at sustainable part-time pace; the microservices premium (~3–5× over a monolith) was accepted knowingly for its learning value. Phases end at demonstrable, testable milestones.

## Phase 1 — Skeleton & spine (≈ 2–3 weeks)

**Goal:** all infrastructure patterns proven on trivial functionality, so no later phase fights plumbing.

**Deliverables**
- ✅ Solution layout: 6 service stubs + gateway + Notifications worker + `BuildingBlocks` (contracts, outbox, OTel, internal-claims auth)
- ✅ `docker-compose.infra.yml` (Postgres 17, RabbitMQ + management UI)
- ✅ MassTransit + EF outbox wired end-to-end with one ping-pong event between two services
- ✅ YARP routing `/api/{service}/*`; OpenTelemetry trace spanning gateway → service → consumer
- ✅ CI: build, test, Dockerfile build per service

**Validation:** one HTTP request through the gateway produces a single distributed trace including a RabbitMQ hop; killing the consumer and restarting redelivers via outbox.

## Phase 2 — Identity & Catalog (≈ 3–4 weeks)

**Goal:** users can exist and products can be found.

**Deliverables**
- ✅ Identity: register/login/logout, Argon2id, sessions, email verification + password reset (via Notifications), roles; gateway session validation + internal claims minting; lockout/rate limits
- ✅ Catalog: product/variant/category schema, `ISupplierImporter` + sample importer seeding ≥ 10k SKUs, import-run tracking
- ✅ Search: tsvector + pg_trgm + JSONB filters behind `ISearchProvider`
- ✅ Storefront v0: Next.js with SSR category/product/search pages against the gateway

**Validation:** FR-1, FR-2, FR-8 pass; NFR-5 (search p95 < 500 ms at 10k SKUs) measured; NFR-6 cookie/hashing tests green.

## Phase 3 — Money: checkout saga, ledger, refunds (≈ 4–6 weeks) — *the hard phase*

**Goal:** a cent can move in, around, and back out — correctly, idempotently, observably.

**Deliverables**
- ✅ Ordering: anonymous cart, login merge, checkout saga (state machine + timeouts + compensation), per-line `FulfillmentSource`, order projections (event-fed product copies)
- ✅ Payments: double-entry ledger (+ balance constraint), Stripe adapter (Payment Intents, test mode), webhook inbox with signature verification + dedup, `ITaxStrategy` flat placeholder
- ✅ Storefront checkout: Stripe Payment Element, confirmation page, guest→account conversion
- ✅ Refund execution path in Payments (saga-callable), admin-initiated refunds
- ✅ Chaos test: kill Payments mid-checkout → saga recovers to terminal state

**Validation:** FR-3–FR-7 pass; NFR-1–NFR-3 enforced by automated tests; a full guest purchase + refund works end-to-end on Stripe test cards.

## Phase 4 — Operations: Fulfillment, Support/RMA, Admin, Xero (≈ 4–5 weeks)

**Goal:** the store can be *run*, not just bought from.

**Deliverables**
- ✅ Fulfillment: shipment records per fulfillment source, tracking assignment → events → emails
- ✅ Support: order-linked tickets, RMA state machine, refund saga wired Support → Payments
- ✅ Blazor Server admin: catalog CRUD, import dashboard, orders, RMA queue, refunds; admin role + subdomain/IP posture
- ✅ Xero: OAuth2 app, nightly summary journals + per-refund postings, sync-run monitoring
- ✅ OWASP ASVS L1 self-audit of Identity; dependency scanning in CI

**Validation:** FR-9–FR-13 pass; the full MVP success scenario (TL;DR metrics) demonstrated start-to-finish; Xero demo org shows balanced daily journal + refund entry matching the ledger.

## Post-MVP (not scheduled)

Kubernetes deployment learning track, company registration → live Stripe keys + real tax strategy, first real supplier integration, then [13-future-considerations.md](./13-future-considerations.md).
