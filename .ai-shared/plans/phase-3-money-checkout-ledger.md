# Feature: Phase 3 — Money: cart, checkout saga, double-entry ledger, Stripe, refund execution

> **PRE-EXECUTION NOTE:** written before Phases 1–2 executed. Verify prior-phase artifacts (BuildingBlocks auth/messaging helpers, Identity sessions, Catalog events) and adjust names/paths to reality before implementing. This is the hardest phase — re-read ADR-0007 and ADR-0014 in full first.

The following plan should be complete, but its important that you validate documentation and codebase patterns and task sanity before you start implementing.

Pay special attention to naming of existing utils types and models. Import from the right files etc.

## Feature Description

A cent can move in, around, and back out — correctly, idempotently, observably: anonymous cart in Ordering, checkout as a MassTransit saga, append-only double-entry ledger in Payments as the source of truth, Stripe (test mode) Payment Intents + webhook inbox, refund execution path, storefront checkout with Stripe Payment Element, guest→account conversion, and the chaos test proving saga recovery.

## User Story

As a shopper
I want to add items to a cart and pay by card (or Apple/Google Pay) as a guest, and get my money back when refunded
So that I can actually buy things — while every cent is accounted for in a balanced ledger.

## Problem Statement

Phases 1–2 give identity and findable products but nothing can be bought. Money flows are where distributed systems fail dangerously: dual writes, double charges, unbalanced books, webhook races.

## Solution Statement

Checkout is an orchestrated saga in Ordering (state machine with timeouts/compensation); Payments owns an append-only double-entry ledger with a DB-enforced balance invariant and the only Stripe integration; all money mutations are idempotent; webhooks reconcile via a deduplicating inbox. Refund execution lands here (admin-initiated); the Support-driven RMA flow arrives in Phase 4 and reuses this path.

## Feature Metadata

**Feature Type**: New Capability
**Estimated Complexity**: High (the hard phase — sagas + money correctness)
**Primary Systems Affected**: Ordering, Payments, Storefront, Gateway (routes), Contracts, Notifications
**Dependencies**: Stripe.net, Stripe test account + CLI (`stripe listen` for local webhooks), MassTransit saga + EF saga persistence

---

## CONTEXT REFERENCES

### Relevant Codebase Files IMPORTANT: YOU MUST READ THESE FILES BEFORE IMPLEMENTING!

- `docs/adr/0007-masstransit-rabbitmq-outbox-sagas.md` — Why: saga orchestration + outbox rules are binding here.
- `docs/adr/0014-stripe-only-v1-double-entry-ledger.md` — Why: ledger-as-truth, IPaymentProvider shape, SAQ-A constraint, refund-only-via-saga rule.
- `docs/adr/0003-per-line-item-fulfillment-source.md` — Why: order line schema requirement.
- `docs/adr/0015-no-legal-entity-test-mode-config-seams.md` — Why: test keys, `STORE_CURRENCY` config, `ITaxStrategy` placeholder.
- `docs/prd/3commerce/10-api-specification.md` — Why: checkout request/response contract (201 + clientSecret + minor-unit totals).
- `docs/reference/api.md` §3/§7 — Why: saga status-code rule (never block HTTP on saga), Idempotency-Key filter.
- Phase 1/2 BuildingBlocks + Identity cookie/claims flow — Why: guest cart cookie + optional auth on checkout.

### New Files to Create

- Contracts: `Ordering/{CartSubmitted,OrderConfirmed,OrderCancelled}.cs`, `Payments/{AuthorizePayment,PaymentSucceeded,PaymentFailed,RefundRequested,RefundCompleted}.cs`
- Ordering: `Domain/{Cart,CartItem,Order,OrderLine(FulfillmentSource!),OrderStatus}.cs`, `Infrastructure/Sagas/CheckoutStateMachine.cs` + `CheckoutState.cs` (EF saga persistence), `Infrastructure/Projections/ProductCopyConsumer.cs` (consumes `ProductUpserted` → local product name/price copy), `Api/Endpoints/{Cart,Checkout,Orders}Endpoints.cs`
- Payments: `Domain/Ledger/{LedgerAccount,JournalEntry,JournalLine}.cs` (+ CHECK constraint migration: per-entry Σdebits=Σcredits), `Domain/IPaymentProvider.cs`, `Domain/ITaxStrategy.cs` + `FlatRateTaxStrategy.cs`, `Infrastructure/Stripe/StripePaymentProvider.cs`, `Infrastructure/Webhooks/{StripeWebhookEndpoint,WebhookInbox}.cs`, `Infrastructure/Consumers/{AuthorizePaymentConsumer,ExecuteRefundConsumer}.cs`, `Api/Endpoints/AdminLedgerEndpoints.cs`, `Api/Endpoints/AdminRefundEndpoints.cs`
- BuildingBlocks: `Web/IdempotencyKeyFilter.cs` (replay returns original response from stored hash)
- Storefront: `app/(shop)/cart/page.tsx`, `app/(shop)/checkout/{page,confirmation}.tsx`, `components/checkout/PaymentElementWrapper.tsx` ('use client' leaf), Server Actions `addToCart/updateCart/submitCheckout`, guest→account conversion UI on confirmation
- Tests: `tests/3commerce.IntegrationTests/{CheckoutSagaTests,LedgerInvariantTests,WebhookDedupTests,ChaosTests}.cs`

### Relevant Documentation YOU SHOULD READ THESE BEFORE IMPLEMENTING!

- [MassTransit — saga state machines](https://masstransit.io/documentation/patterns/saga/state-machine) — Why: state/event/timeout/compensation syntax; EF repository for saga state.
- [Stripe — Payment Intents](https://docs.stripe.com/payments/payment-intents) and [Payment Element](https://docs.stripe.com/payments/payment-element) — Why: intent lifecycle (`requires_payment_method→processing→succeeded`), client confirmation flow.
- [Stripe — webhook signature verification](https://docs.stripe.com/webhooks#verify-official-libraries) + [Stripe CLI local listener](https://docs.stripe.com/stripe-cli/overview) — Why: local webhook forwarding to `localhost:5104`.
- [Stripe — idempotent requests](https://docs.stripe.com/api/idempotent_requests) — Why: pass our Idempotency-Key through to Stripe calls.
- Double-entry modeling refresher: [Books, an immutable ledger design (Square/Block engineering)](https://developer.squareup.com/blog/books-an-immutable-double-entry-accounting-database-service/) — Why: account/entry/line schema sanity-check.

### Patterns to Follow

- **Saga flow (canonical):** `POST /checkout` → create Order(Pending) + raise `CartSubmitted` (outbox, same txn) → saga: `AuthorizePayment` → Payments creates PaymentIntent + ledger `authorization` memo → responds `PaymentRequested` + clientSecret (stored on saga state, returned via the original HTTP response synchronously from intent creation — the HTTP call returns once the intent exists, NOT when payment completes) → browser confirms via Payment Element → Stripe webhook `payment_intent.succeeded` → Payments posts balanced journal entry → `PaymentSucceeded` → saga → `OrderConfirmed`. Timeout (e.g. 30 min) → cancel intent → `OrderCancelled`.
- **Ledger discipline:** chart of accounts seeded (`cash.stripe`, `revenue.sales`, `revenue.refunds`, `expense.stripe_fees`, `liability.tax_collected`); every post is one JournalEntry with ≥2 lines; append-only (no UPDATE/DELETE grants on those tables for the service role).
- **Money:** `long` minor units + currency code everywhere (AGENTS.md invariant); totals computed server-side only; cart prices re-validated against Catalog copy at submit.

---

## IMPLEMENTATION PLAN

### Phase 1: Foundation
Contracts; Ordering cart domain + endpoints; Payments ledger schema + invariant + chart seed; `ITaxStrategy` flat impl.

### Phase 2: Core Implementation
Checkout saga state machine; `IPaymentProvider` + Stripe adapter; webhook inbox + reconciliation; refund consumer.

### Phase 3: Integration
Storefront cart/checkout/confirmation with Payment Element; guest conversion; gateway routes; Notifications order-confirmation email.

### Phase 4: Testing & Validation
Saga outcome matrix (success/declined/timeout), ledger property tests, webhook dedup, chaos test (kill Payments mid-checkout), Idempotency-Key replay.

---

## STEP-BY-STEP TASKS

### 1. CREATE message contracts (Ordering/Payments)
- **IMPLEMENT**: records listed above; additive-only rule applies from now on.
- **VALIDATE**: `dotnet build src/BuildingBlocks/Contracts`

### 2. CREATE Payments ledger domain + migration with balance constraint
- **IMPLEMENT**: tables + `CHECK` via trigger/constraint ensuring per-entry Σdebit=Σcredit and non-negative lines; revoke UPDATE/DELETE on journal tables from service role in migration; seed chart of accounts.
- **GOTCHA**: enforce balance at the **entry** level in one txn — deferred constraint trigger (`CONSTRAINT TRIGGER ... INITIALLY DEFERRED`) is the standard Postgres approach.
- **VALIDATE**: unit test: unbalanced insert throws; `psql` manual UPDATE on journal_lines → permission denied

### 3. CREATE ITaxStrategy + FlatRateTaxStrategy; IPaymentProvider + StripePaymentProvider
- **IMPLEMENT**: provider ops: `CreateIntentAsync(orderId, amountMinor, currency, idempotencyKey)`, `RefundAsync(...)`, `ParseWebhook(payload, signature)`; Stripe test keys from config; forward idempotency keys to Stripe.
- **VALIDATE**: provider unit tests against Stripe test API (Category=External, skippable offline)

### 4. CREATE Ordering cart (anonymous + merge) + endpoints
- **IMPLEMENT**: cookie-keyed cart per PRD §10; price snapshot at add; merge-on-login consumer of Identity login event OR merge in endpoint when claims present; `ProductCopyConsumer` projection (name/price/image from `ProductUpserted`).
- **VALIDATE**: integration: add item anonymously → login → cart contains item

### 5. CREATE CheckoutStateMachine saga + checkout endpoint
- **IMPLEMENT**: states `OrderPending/PaymentRequested/Confirmed/Cancelled`; events `CartSubmitted/PaymentSucceeded/PaymentFailed`; 30-min timeout schedule → compensation (cancel intent, restore nothing — stock not reserved in v1); endpoint per PRD §10 returns 201 + clientSecret + minor-unit totals; per-line `FulfillmentSource=Unassigned`.
- **GOTCHA**: saga state persisted via EF in ordering_db; correlate by OrderId; the HTTP response needs the clientSecret — implement request/response (`RequestClient<AuthorizePayment>`) for intent creation inside the endpoint, then let the saga own the async remainder. Never hold the HTTP request past intent creation (api.md §3).
- **VALIDATE**: integration: submit checkout → saga row in `PaymentRequested`; simulated `PaymentSucceeded` → `Confirmed` + `OrderConfirmed` event observed

### 6. CREATE Stripe webhook endpoint + inbox + reconciliation
- **IMPLEMENT**: `POST /webhooks/stripe` (anonymous, signature-verified in service per api.md §7); inbox table keyed by Stripe event id; handlers for `payment_intent.succeeded/payment_failed/refund.updated`; on success: balanced journal entry (cash.stripe / revenue.sales / liability.tax_collected) + fee entry from balance transaction; publish `PaymentSucceeded`.
- **GOTCHA**: webhooks can arrive before the saga expects them (race) — handlers must upsert facts idempotently and publish; saga handles out-of-order via state machine guards.
- **VALIDATE**: `stripe listen --forward-to localhost:5104/webhooks/stripe` + `stripe trigger payment_intent.succeeded`; resend same event → single journal entry (WebhookDedupTests)

### 7. CREATE refund execution path (admin-initiated)
- **IMPLEMENT**: `ExecuteRefundConsumer` (consumes `RefundRequested` — Phase 4's RMA will publish it): ledger reversal entry + `RefundAsync` + on webhook confirmation `RefundCompleted`; `POST /admin/refunds` (admin role, Idempotency-Key required) publishes the same contract — one path only (ADR-0014).
- **VALIDATE**: integration: confirmed order → admin refund → ledger shows balanced reversal; Stripe test dashboard shows refund; replay with same key → no second refund

### 8. ADD IdempotencyKeyFilter to BuildingBlocks + apply to money endpoints
- **IMPLEMENT**: filter persisting (key, request-hash, response) per service; conflict on same key + different body → 422.
- **VALIDATE**: unit + integration replay tests

### 9. CREATE storefront cart/checkout/confirmation
- **IMPLEMENT**: cart page (server-rendered from Ordering, `useOptimistic` add/remove), checkout page (guest email+address per ADR-0013, ≤3 screens), `PaymentElementWrapper` client leaf with clientSecret, confirmation page polling order status (pending-first per components.md §2) + "set a password" guest conversion (calls Identity `/convert-guest`).
- **GOTCHA**: Stripe publishable key is the ONLY Stripe value allowed client-side.
- **VALIDATE**: manual: full purchase with test card `4242 4242 4242 4242`; declined card `4000 0000 0000 0002` shows error and order ends `Cancelled`

### 10. CREATE chaos + ledger property tests
- **IMPLEMENT**: ChaosTests — kill Payments host after intent creation, deliver webhook, restart → saga reaches `Confirmed` (NFR-2); LedgerInvariantTests — property-based: any sequence of sale/refund/fee postings keeps every entry balanced + trial balance = 0 (NFR-1).
- **VALIDATE**: `dotnet test tests/3commerce.IntegrationTests --filter Category=Integration`

### 11. ADD Notifications order-confirmation email + UPDATE docs
- **IMPLEMENT**: consume `OrderConfirmed` → email with signed order link (guest access per PRD §10); export OpenAPI contracts → `docs/api/` + index; status file updates.
- **VALIDATE**: email visible in sandbox after test purchase; `ls docs/api/` includes ordering/payments contracts

---

## TESTING STRATEGY

### Unit Tests
Ledger entry construction/balance; tax strategy rounding (banker's rounding documented); saga state machine via MassTransit TestHarness (all transition paths); cart price-revalidation logic.

### Integration Tests
Checkout outcome matrix (success/declined/timeout→cancel); webhook dedup + out-of-order arrival; refund single-path; Idempotency-Key replay; guest conversion attaches order.

### Edge Cases
Webhook before saga state exists; duplicate `CartSubmitted` (double-click submit); price changed between cart-add and checkout (re-validate → 409 with updated totals); zero-decimal currencies (JPY) in money helper; partial refund > remaining balance → rejected.

---

## VALIDATION COMMANDS

### Level 1: Syntax & Style
```bash
dotnet build 3commerce.sln && dotnet format --verify-no-changes
cd src/Storefront && npm run lint && npx tsc --noEmit
```
### Level 2: Unit Tests
```bash
dotnet test 3commerce.sln --filter Category!=Integration
```
### Level 3: Integration Tests
```bash
dotnet test tests/3commerce.IntegrationTests --filter Category=Integration
```
### Level 4: Manual Validation
```bash
stripe listen --forward-to localhost:5104/webhooks/stripe   # separate terminal
# Browser: search → add to cart → checkout as guest → 4242 card → confirmation
# Verify: order Confirmed; psql payments_db: journal entries balanced; refund from admin endpoint; money back in Stripe dashboard
```

---

## ACCEPTANCE CRITERIA

- [ ] FR-3–FR-7 pass; NFR-1/2/3 enforced by automated tests (see PRD §11)
- [ ] Full guest purchase + admin refund works end-to-end on Stripe test cards
- [ ] Ledger append-only verified at DB-permission level; trial balance zero after test suite
- [ ] Checkout HTTP returns at intent creation (never blocks on saga completion)
- [ ] One trace covers storefront → gateway → Ordering → Payments → webhook → Confirmed (NFR-7)
- [ ] No card data fields anywhere server-side (SAQ-A grep audit)

## COMPLETION CHECKLIST

- [ ] All tasks completed in order; validations passed immediately
- [ ] Chaos test green reproducibly (run 3×)
- [ ] Full suite + lint/typecheck clean
- [ ] `docs/api/` contracts updated; status file updated per task

## NOTES

- Stock reservation is deliberately absent in v1 (no supplier yet) — do not invent it.
- Currency from `STORE_CURRENCY` config; tax via FlatRateTaxStrategy — never hardcode (ADR-0015).
- Phase 4's RMA publishes the same `RefundRequested` contract — keep it generic (requestedBy, orderId, lines[], amountMinor, reason).
- Confidence: 6/10 — saga/webhook race handling and request/response-within-saga are genuinely tricky; expect iteration on Task 5/6. Budget accordingly.
