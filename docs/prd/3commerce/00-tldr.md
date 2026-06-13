# 0. TL;DR

## Problem
The owner wants to (a) deeply learn distributed-systems engineering on a production-grade .NET stack and (b) end up with a launchable e-commerce business selling physical goods sourced from large third-party catalogs. Off-the-shelf platforms (Shopify, Medusa) would serve goal (b) but defeat goal (a).

## Target user
- **Shoppers** buying physical products online (guest checkout supported; accounts optional).
- **Store operator/admin** (initially the owner) managing catalog, orders, RMAs, refunds, and supplier feeds.
- **The builder** (solo C# developer) — the learning outcome is itself a first-class deliverable.

## Proposed solution
A six-service C# microservices platform — **Identity, Catalog, Ordering, Payments, Fulfillment, Support** — communicating via MassTransit + RabbitMQ (async-first, transactional outbox, sagas for checkout and refunds), each owning its own PostgreSQL database. A **YARP gateway** is the single public origin; a **Next.js (SSR)** storefront delivers the customer UX; a **Blazor Server** app serves admin. Auth is custom-built (opaque HttpOnly session cookie at the edge, signed internal claims). Money runs through a custom **double-entry ledger** with Stripe as the v1 payment rail behind an `IPaymentProvider` abstraction, syncing nightly summary journals to **Xero**.

## Success metrics
- A complete guest checkout (cart → Stripe test payment → order confirmed → ledger balanced) executes across services with no manual intervention.
- A refund flows Support → Payments → Stripe → Xero as a saga, with the ledger and Xero in agreement.
- Supplier catalog import of ≥ 10,000 sample SKUs is searchable with < 500 ms p95 search latency.
- Every money event in the ledger is double-entry balanced (sum of debits = sum of credits, enforced by test suite).
- Each service deploys and runs independently; killing any one service does not corrupt in-flight orders (sagas recover).

## Out of scope (v1)
- Live payments / production launch (blocked on company registration — no legal entity yet).
- Polar or any second payment adapter, MFA, social login, live chat, knowledge base, multi-currency display, marketplace/third-party sellers, Kubernetes production deployment.
