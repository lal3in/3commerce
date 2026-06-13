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
- ❌ Multi-currency pricing/display
- ❌ Discount codes / promotions engine
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
- ❌ Stripe Tax / OSS / multi-jurisdiction tax logic

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
