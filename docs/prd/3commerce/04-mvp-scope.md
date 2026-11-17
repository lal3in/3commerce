# 4. MVP Scope

> The authoritative in/out list. When tempted to add something from ❌, read [13-future-considerations.md](./13-future-considerations.md) first.

## Core Functionality

### Catalog & Search
- ✅ Neutral internal product schema (products, variants, categories, JSONB attributes, images)
- ✅ `ISupplierImporter` interface + one sample-data importer seeding ≥ 10,000 SKUs
- ✅ Postgres full-text search (tsvector) + pg_trgm typo tolerance behind `ISearchProvider`
- ✅ Category browse + attribute filtering
- ❌ Real supplier feed integration (no supplier signed yet)
- ❌ Dedicated search engine (Meilisearch/Typesense/Elastic)
- ❌ Product reviews, ratings, recommendations

### Cart & Checkout
- ✅ Anonymous cookie-keyed cart (lives in Ordering service)
- ✅ Guest checkout (email + shipping address only)
- ✅ Post-purchase account conversion ("set a password to track your order")
- ✅ Checkout saga: order → payment authorization → confirmation (MassTransit state machine)
- ✅ Fulfillment source tracked **per line item** (dropship vs warehouse undecided)
- ✅ Single configurable currency; flat/configurable shipping rates; worldwide shipping modeled as DAP
- ❌ Multi-currency pricing/display *(out of MVP scope; **delivered post-MVP** — per-currency shelf prices [ADR-0038] and the currency registry [ADR-0046])*
- ❌ Discount codes / promotions engine *(out of MVP scope; **delivered post-MVP** — storefront-wide items discount, threshold promotions [ADR-0051], coupon codes [ADR-0052])*
- ❌ Real-time carrier rate quotes

### Accounts (custom-built Identity)
- ✅ Registration, login, logout — opaque session token in Secure/HttpOnly/SameSite cookie
- ✅ Argon2id password hashing via vetted library (never hand-rolled crypto)
- ✅ Email verification, password reset
- ✅ Profile + saved addresses, order history
- ✅ Admin role claim (gates Blazor admin)
- ❌ MFA (TOTP), social login (Google/Apple), passkeys
- ❌ Account deletion self-service (manual on request in v1; GDPR tooling later)

### Payments & Ledger
- ✅ Custom double-entry ledger — source of truth for all money events
- ✅ `IPaymentProvider` abstraction with **Stripe adapter only** (Payment Intents; Apple/Google Pay included)
- ✅ Stripe webhook ingestion with signature verification + idempotent processing
- ✅ Refund execution (full and partial) via saga
- ✅ Stripe **test mode only** (no legal entity yet — live keys are a launch task, not a build task)
- ✅ `ITaxStrategy` interface with flat home-rate placeholder implementation
- ❌ Polar adapter (digital-only MoR — cannot process physical goods; future digital line only)
- ❌ Second live payment rail (PayPal/Adyen/Mollie)
- ❌ Stripe Tax / multi-jurisdiction tax engine (an OSS-aware `ITaxStrategy` is a prerequisite of EU cross-border invoicing — ADR-0063 P1b) *(the OSS-aware strategy is **delivered post-MVP** — `EuOssTaxStrategy`, configured per storefront by its home Member State: home rate up to the EUR 10,000 EU-wide threshold, destination rate above it, per-line destination for mixed goods/digital carts, special VAT territories and Northern Ireland, OSS subscriptions renewing at the destination's rate; standard rates only, and EU cross-border B2C go-live is gated on verifying the rate table against the Commission's TEDB)*

### Tax documents (post-MVP, [ADR-0063](../../adr/0063-customer-tax-invoices-and-credit-notes.md))

> **Status (2026-11-15):** in scope by owner approval. The phase-0 prerequisites are delivered — per-line tax facts, the
> OSS-aware tax strategy, seller tax identity and establishments, buyer identity at checkout (bill-to, optional business
> name + tax ID, a saved default on the account), renewal and usage tax with settled-charge events, and the invoicing
> facts on the order and refund events. The Invoicing service itself (phase 1) is not built yet, so no document is
> issued today.

- ✅ Customer tax invoices (full / simplified per jurisdiction) issued at payment capture, for orders, renewals and usage charges
- ✅ Credit notes for refunds / RMA referencing the original invoice; issued documents immutable
- ✅ Gapless, chronological numbering per series (seller × establishment × document type × year) — distinct from the gap-tolerant order number
- ✅ Jurisdiction profiles: EU-BASE (VAT Directive common rule set) + Australia + generic; home Member State profile configured after company registration; readiness gate per storefront
- ✅ PDF delivery via account page / signed link; 10-year WORM retention
- ❌ Structured e-invoicing / clearance (Peppol, SdI, KSeF, FR PA, VeriFactu) — phase 3
- ❌ Reverse charge / VIES B2B — phase 2;  ❌ marketplace / deemed-supplier model

### Support & RMA
- ✅ Order-linked support tickets with typed reasons (where-is-it, damaged, refund request)
- ✅ RMA state machine: requested → approved/denied → (return shipped) → refund issued
- ✅ Refund saga: Support → Payments → ledger → Stripe → Xero
- ✅ Email notifications both directions
- ❌ Live chat, knowledge base, agent assignment, SLAs

### Admin (Blazor Server)
- ✅ Catalog CRUD + supplier import monitoring (counts, validation errors)
- ✅ Order list/detail (payment state, per-line fulfillment source, ticket history)
- ✅ RMA approval queue + refund issuing
- ✅ Admin role enforcement; separate subdomain + IP allowlist
- ❌ Dashboards/analytics, staff accounts with granular permissions

## Technical

- ✅ Six services: Identity, Catalog, Ordering, Payments, Fulfillment, Support
- ✅ MassTransit + RabbitMQ, async-first; EF Core transactional outbox; idempotent consumers
- ✅ One Postgres container, **one database per service**, EF Core migrations per service, no cross-service joins
- ✅ Cross-service read models maintained via events (e.g. Ordering keeps product name/price copies)
- ✅ YARP gateway: single public origin, session validation, internal signed-claims forwarding, rate limiting
- ✅ Next.js SSR storefront (SEO for thousands of product pages)
- ✅ OpenTelemetry traces/logs/metrics across all services
- ✅ xUnit + Testcontainers integration tests; ledger balance invariant tests
- ❌ Kubernetes manifests/deployment (post-MVP learning phase)
- ❌ Event sourcing, Kafka, Dapr
- ❌ CQRS frameworks (plain handlers; read models only where events require them)

## Integration

- ✅ Stripe (test mode): Payment Intents, refunds, webhooks
- ✅ Xero: OAuth2 app, nightly summary journals (sales, refunds, fees, payouts) + per-refund/dispute postings
- ✅ Transactional email provider behind `IEmailSender` (verification, order, RMA emails)
- ❌ Polar, carrier/shipping APIs, supplier APIs/EDI

## Deployment

- ✅ Local: `dotnet run` per service; Postgres + RabbitMQ via docker compose
- ✅ Dockerfiles per service (build verification; used later for k8s)
- ❌ Production hosting, CI/CD to cloud, Kubernetes (deferred until post-MVP)
