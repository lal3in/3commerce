# 11. Success Criteria

## MVP success definition

The MVP is done when a shopper can find a product among ≥ 10,000 seeded SKUs, buy it as a guest with a Stripe test card, receive confirmation and shipping emails, and get a refund through the RMA flow — with every step traversing the six services via RabbitMQ, the ledger balanced to the cent, and the corresponding entries visible in Xero (demo org).

## Functional requirements

- ✅ **FR-1** Catalog import seeds ≥ 10,000 SKUs via `ISupplierImporter`, with per-run accepted/rejected counts visible in admin.
- ✅ **FR-2** Search returns typo-tolerant (`pg_trgm`), attribute-filterable results.
- ✅ **FR-3** Anonymous cart persists across visits (cookie) and merges into the user cart on login.
- ✅ **FR-4** Guest checkout completes with email + shipping address only; no forced registration.
- ✅ **FR-5** Checkout runs as a MassTransit saga reaching a terminal state (`OrderConfirmed`/`OrderCancelled`) in all payment outcomes (success, declined, timeout).
- ✅ **FR-6** Every order line item carries a fulfillment source (`Unassigned` allowed in v1).
- ✅ **FR-7** Guests can convert to accounts post-purchase; prior orders attach to the account.
- ✅ **FR-8** Accounts support email verification, password reset, addresses, and order history.
- ✅ **FR-9** Customers open order-linked tickets; RMA flows `Requested → Approved/Denied → … → RefundIssued`.
- ✅ **FR-10** Approved RMA executes the refund saga: ledger reversal + Stripe refund + Xero posting; double-approval has no double effect (idempotent).
- ✅ **FR-11** Nightly Xero job posts one balanced summary journal per day with sales, refunds, and fee lines.
- ✅ **FR-12** Admin (Blazor) supports catalog CRUD, order inspection, import monitoring, RMA queue, refund issuing — gated by admin role.
- ✅ **FR-13** Transactional emails fire on: verification, reset, order confirmation, tracking assignment, RMA state changes.

## Non-functional requirements

- ✅ **NFR-1 (Ledger invariant)** Every ledger transaction balances; enforced by DB constraint and tests. Zero unbalanced entries, ever.
- ✅ **NFR-2 (Resilience)** Killing any single service mid-checkout and restarting it leads the saga to a correct terminal state — verified by an automated chaos test.
- ✅ **NFR-3 (Idempotency)** Replaying any RabbitMQ message or Stripe webhook produces no duplicate side effects — verified by tests.
- ✅ **NFR-4 (Isolation)** No service queries another service's database (no cross-DB connection strings present); cross-service data arrives via events only.
- ✅ **NFR-5 (Performance)** Search p95 < 500 ms and product page SSR p95 < 800 ms locally at 10k SKUs.
- ✅ **NFR-6 (Security)** Cookie flags (`Secure/HttpOnly/SameSite`), Argon2id parameters, gateway header-stripping, and rate limiting verified by tests; Identity self-audited against OWASP ASVS L1 before any live traffic.
- ✅ **NFR-7 (Observability)** One checkout = one OpenTelemetry trace spanning gateway → Ordering → Payments → Notifications.
- ✅ **NFR-8 (Revocation)** Logout/password-reset invalidates sessions within ≤ 60 s everywhere (gateway cache bound).

## Quality indicators

- Integration tests (Testcontainers: real Postgres + RabbitMQ) cover the checkout saga, refund saga, and webhook dedup paths.
- Each service builds and runs in isolation; `docker compose -f docker-compose.infra.yml up` + `dotnet run` × N is the complete local setup.
- Message contracts change additively only; no consumer breaks on a contract version bump.

## UX goals

- Storefront feels "boutique-grade": consistent design system, instant search feedback, skeleton loading, no layout shift on product grids.
- Checkout in ≤ 3 screens (cart → details → pay) with Apple/Google Pay one-tap where available.
- A refund request takes a customer < 2 minutes from order page to confirmation email.
