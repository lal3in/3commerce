# 7. Tools/Features

Detailed feature specifications, grouped by owning service.

## Identity service

| Feature | Specification |
|---|---|
| Registration | Email + password (Argon2id via vetted library; per-user salt; tuned memory/iterations). Email verification token (single-use, expiring) sent via Notifications worker. |
| Login / sessions | Opaque 256-bit random session token, stored server-side (session table: user, created, expires, revoked). Cookie: `Secure; HttpOnly; SameSite=Lax`. Logout = revoke row. Sliding expiry. |
| Session introspection | Endpoint for the gateway; gateway caches positive results ≤ 60 s, so revocation takes effect within a minute. |
| Password reset | Single-use expiring token by email; on reset, revoke all sessions. |
| Guest conversion | Order-confirmation email link sets password → account created → guest orders (matched by verified email) attached. |
| Roles | `customer` (default), `admin`. Carried in internal claims. |
| Account lockout | Progressive delay after repeated failures per account + per IP (rate-limited additionally at gateway). |

## Catalog service

| Feature | Specification |
|---|---|
| Product model | Product → variants (SKU, price, stock-state) → category tree; freeform attributes in JSONB; images by URL. Neutral schema — no supplier-specific fields outside `supplier_ref` JSONB. |
| `ISupplierImporter` | Contract: `ImportRunResult Run(stream/source)` producing upserts + rejections with reasons. v1 implementation: sample-data generator seeding ≥ 10k SKUs. Import runs are persisted (read/accepted/rejected counts) for the admin dashboard. |
| Search | `ISearchProvider` v1 = Postgres: tsvector (weighted title/brand/description) + pg_trgm for typos + JSONB attribute filters + category scoping. Emits `ProductUpserted`/`ProductPriceChanged` events that feed other services' read copies. |

## Ordering service

| Feature | Specification |
|---|---|
| Cart | Anonymous, keyed by cart cookie; merged into user cart on login. Line items snapshot price at add-time; re-validated against Catalog copy at checkout. |
| Checkout saga | State machine: `CartSubmitted → OrderPending → PaymentRequested → PaymentSucceeded/Failed → OrderConfirmed/OrderCancelled`. Timeout + compensation on payment failure. Address + email captured for guests. |
| Per-line fulfillment source | Every line item carries `FulfillmentSource` (enum: `Unassigned`, `Dropship(supplierId)`, `OwnWarehouse`) — fulfillment model is undecided, so this is a day-one schema decision, not a retrofit. |
| Order projection | Order history view holds copied product names/images (event-fed) — order pages never call Catalog. |

## Payments service

| Feature | Specification |
|---|---|
| Double-entry ledger | Accounts (cash-stripe, sales, refunds, fees, tax-collected…), journal entries with ≥ 2 balanced lines; append-only; DB constraint enforces balance. Source of truth for all money facts. |
| `IPaymentProvider` | v1: Stripe Payment Intents (cards + Apple/Google Pay). Operations: create-intent, capture, refund (full/partial), parse-webhook. Polar/PayPal/Adyen are future adapters — **not built in v1**. |
| Webhooks | Signature-verified, deduplicated by Stripe event ID into an inbox table, then processed idempotently; reconciles intent state with saga expectations. |
| Refunds | Executed only via refund saga from Support (or admin-initiated). Ledger reversal entry + Stripe refund + Xero posting. |
| `ITaxStrategy` | v1: configurable flat home-rate placeholder (no legal entity yet). Swap target: Stripe Tax / OSS logic post-registration. |
| Xero sync | Nightly job posts one summary journal per day (sales, refunds, Stripe fees, payouts as lines) + individual postings per refund/dispute. OAuth2 with token refresh; retry with backoff; sync state persisted. Respects rate limits (60/min, 5k/day). |

## Fulfillment service

| Feature | Specification |
|---|---|
| Order intake | Consumes `OrderConfirmed`; creates shipment records grouped by per-line fulfillment source. |
| Supplier forwarding | v1: manual/simulated forwarding (no supplier signed); interface mirrors `ISupplierImporter` philosophy so a real supplier API/email flow plugs in. |
| Tracking | Tracking number entry (admin) → `TrackingAssigned` event → customer email + order page update. |

## Support service

| Feature | Specification |
|---|---|
| Tickets | Order-linked, typed reasons (`WhereIsIt`, `Damaged`, `RefundRequest`, `Other`), message thread, email notifications both ways. No chat/SLA/assignment. |
| RMA state machine | `Requested → Approved/Denied → (AwaitingReturn → ReturnReceived) → RefundIssued`. Approval (admin) triggers the refund saga to Payments. Partial refunds supported per line item. |

## Gateway (YARP)

Routes `/api/{service}/*`, validates session cookie via Identity (cached), mints short-lived internal claims JWT, applies per-IP and per-session rate limits, strips internal headers from inbound requests. Internal services bind to localhost/private network only.

## Storefront (Next.js) & Admin (Blazor Server)

- **Storefront:** SSR product/category/search pages (SEO), cart and checkout flow, account area (orders, addresses, RMA requests), Stripe Elements/Payment Element for card entry. Tailwind-based design system — "great graphic UX" is an explicit requirement.
- **Admin:** catalog CRUD, import-run dashboard, order list/detail (payment + per-line fulfillment + tickets), RMA queue with approve/deny/refund actions. Admin role claim required; separate subdomain + IP allowlist.

## Notifications worker

Consumes domain events → transactional email via `IEmailSender` (verification, password reset, order confirmation, shipment/tracking, RMA state changes). Provider configurable; templates versioned in repo.
