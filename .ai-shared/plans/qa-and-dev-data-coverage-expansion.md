# QA and Dev Data Coverage Expansion Plan

Last Modified Date-Time: 2026-06-30
Status: Planned
Branch when executing: `main` or feature branch `feat/qa-dev-data-coverage`

## Goal

Expand automated coverage and local/demo data so the platform can be exercised across realistic commerce scenarios:

1. Backend unit/integration tests cover high-risk combinations and state transitions across catalog, checkout, payments, fulfillment, support, subscriptions, usage, audit/workflow, and stream/eventing.
2. Playwright storefront, admin, and portal E2E suites cover complete user/operator journeys, not only smoke rendering.
3. `scripts/dev-dummy-data.sh` seeds a rich, repeatable demo tenant with historical products, orders, messages, payments, refunds, shipments, support/RMA, subscriptions, usage, suppliers, payouts, Xero mappings, audit/workflow, marketing, and storefront/customer surfaces.
4. The seeded catalog includes every product/supply/billing class the platform supports.

## Non-goals

- Do not attempt a literal Cartesian product of every field combination. Use a coverage matrix with representative equivalence classes and pairwise/high-risk combinations.
- Do not write directly into service databases for dummy data. Seeding remains API-first unless a service exposes a deliberate dev-only seed endpoint.
- Do not introduce real Stripe/Xero/carrier/provider calls. Dev data must use fake/test/sandbox seams.
- Do not make Playwright depend on brittle generated product names unless the seed profile makes them deterministic.
- Do not expand live `--live` regression so far that normal verification becomes impractical; separate smoke, full, and exhaustive profiles.

## Current coverage baseline

### Storefront Playwright

Current files:

- `src/Storefront/e2e/auth.spec.ts`
- `src/Storefront/e2e/browse.spec.ts`
- `src/Storefront/e2e/cart-checkout.spec.ts`
- `src/Storefront/e2e/screenshots.spec.ts`

Current coverage:

- login/register error path
- home/search/product details
- add-to-cart
- guest checkout with shipping quote + fake payment
- empty cart

### Admin Playwright

Current files:

- `src/Storefront/e2e-admin/admin.spec.ts`
- `src/Storefront/e2e-admin/screenshots.spec.ts`

Current coverage:

- unauthenticated redirect
- dashboard/nav page render
- RMA approve-and-refund to `RefundIssued`

### Dev dummy data

Current `scripts/dev-dummy-data.sh` profiles:

- `core`: catalog import + one demo shopper + one address
- `full`: best-effort API calls for supplier/entity, marketing campaign, one tiered price, one payment account, supplier bank account, Xero mapping, one fulfillment location/carrier, one usage provision/record, one catalog offer
- `mirror-prod`: intentionally unimplemented placeholder

Current gaps:

- no deterministic named scenario products
- no historical orders/transactions/refunds/support tickets/messages
- no seeded subscriptions/usage period lifecycle states
- no broad supply/billing/product-type matrix
- no seeded fulfillment shipments/packages/tracking/dropship orders/holds
- no stable fixtures for Playwright to select products by scenario

## Coverage strategy

Use three profiles:

1. `smoke`: fast, minimal deterministic data needed for every Playwright run.
2. `full`: complete demo tenant and operator data across services.
3. `exhaustive`: slower state-rich profile for nightly/local full regression, including historical transactions and edge states.

Use stable scenario codes and names in dummy data. Example SKU/name prefix: `E2E-Scenario-*` and product tags/metadata where APIs support it.

## Product/supply/billing coverage matrix

| Scenario code | Product type | Supply category | Fulfilment type | Pricing model | Billing mode | Purpose |
|---|---|---|---|---|---|---|
| `physical-warehouse-flat` | Physical | Physical | Warehouse | Flat | OneTime | Standard stocked physical checkout/shipment |
| `physical-dropship-flat` | Physical | Physical | Dropship | Flat | OneTime | Supplier order forwarding |
| `physical-multi-variant-tiered` | Physical | Physical | Warehouse | Tiered/Graduated | OneTime | variant picker, tier pricing, cart quantity |
| `bundle-mixed-physical` | Bundle | Physical | Warehouse + Dropship | Flat | OneTime | grouped fulfillment, multiple shipments |
| `digital-download-onetime` | DigitalDownload | Digital | DigitalDelivery | Flat | OneTime | entitlement/no shipment |
| `subscription-monthly-flat` | Subscription | Digital/Service | DigitalDelivery/ManualService | Flat | Recurring | subscription lifecycle |
| `subscription-yearly-tiered` | Subscription | Digital/Service | DigitalDelivery/ManualService | Tiered | Recurring | annual/tiered billing |
| `usage-api-meter` | Usage | Service/Digital | ApiAccess | Usage | UsageBased | usage balance, overage billing |
| `manual-service-onetime` | ManualService | Service | ManualService | Flat | OneTime | service entitlement/manual fulfillment |
| `out-of-stock-hold` | Physical | Physical | Warehouse | Flat | OneTime | inventory hold/release path |
| `inactive-unpublished-private` | Physical | Physical | Warehouse | Flat | OneTime | search/publication/visibility negative coverage |

## Historical state/data matrix

Seed or test representative records for:

### Orders and checkout

- anonymous cart with multiple variants
- logged-in cart merge after login
- guest checkout confirmed
- account checkout with saved card
- selected shipping quote persisted into totals
- cancelled/unpaid order
- order with inventory hold then release
- mixed physical + digital order
- order history attached to converted account

### Payments/ledger/accounting

- successful authorization/payment
- failed payment/PastDue where supported
- full refund
- partial/per-line RMA refund
- supplier payable accrual/payout instruction
- Xero mapping resolution examples
- daily journal sync run status examples

### Fulfillment

- warehouse shipment created
- dropship supplier order forwarded
- package labelled with fake carrier
- tracking assigned/in transit/delivered
- restock from RMA return
- inventory availability mirrored to catalog

### Support/messages/RMA

- support ticket with customer/operator message thread
- RMA requested
- RMA approved immediate refund
- RMA approved requiring return
- return received and restocked
- denied RMA

### Digital/subscription/usage

- active entitlement
- cancelled/expired/suspended entitlement where API allows
- active subscription
- cancelled subscription
- PastDue subscription/failure state where supported
- usage within allowance
- usage over allowance with overage billing
- usage over allowance rejected when overage disabled

### Operations/audit/workflow/events

- audit entries for mutation, denied attempt, sensitive read/field reveal shape
- workflow/job run succeeded and failed records
- stream outbox rows pending/published/failed in unit/integration coverage
- dead-letter/replay paths in tests, not necessarily seeded UI data

## Test expansion plan

### Backend unit tests

Add/expand targeted unit tests for:

- product/supply/billing compatibility matrix
- pricing edge cases: flat, tiered, recurring period, usage quantity, currency mismatch guards
- cart/checkout line stamping for every product type
- entitlement/subscription/usage gates and idempotency
- support/RMA per-line quantities and restock state transitions
- stream privacy/replay/outbox metrics already added; keep as contract guards

### Backend integration tests

Add cross-service tests for:

- warehouse physical checkout -> stock movement -> shipment
- dropship checkout -> supplier order, no warehouse stock decrement
- digital download checkout -> entitlement, no shipment
- subscription checkout -> subscription + entitlement
- usage product -> provision/record/bill overage
- mixed cart -> shipment + entitlement + subscription/usage side effects
- support ticket message thread + RMA approved/denied/return-received
- account checkout with saved payment method
- guest -> account order attach and order history
- admin payout/Xero/payment-account readiness paths

### Storefront Playwright

Add Playwright tests split by file and profile:

- `catalog-product-types.spec.ts`
  - deterministic scenario products visible/searchable
  - product detail renders variant, digital/service/subscription/usage affordances
  - private/unpublished products not visible
- `cart-checkout-matrix.spec.ts`
  - physical warehouse checkout
  - physical dropship checkout
  - mixed physical + digital checkout
  - subscription checkout
  - usage checkout
  - account checkout with saved card
- `account-access.spec.ts`
  - order history
  - saved addresses
  - saved payment methods
  - entitlements/subscriptions/usage pages once frontend affordances exist
- `support-rma.spec.ts`
  - ticket creation/message thread
  - per-line RMA request from order page
  - confirmation/status views
- `marketing-consent.spec.ts`
  - consent banner behavior
  - analytics/marketing opt-in/out storage
  - campaign/UTM-safe navigation where storefront edge capture exists

### Admin Playwright

Add admin tests split by domain:

- `catalog-admin.spec.ts`
  - create/edit/publish/unpublish product
  - offer/pricing combinations
  - product publication readiness
- `commerce-ops.spec.ts`
  - storefront/domain/product publication ops
  - carrier/default configuration
  - inventory stock feed and restock
- `orders-fulfillment.spec.ts`
  - order detail/list filters
  - hold/release
  - shipment package/label/tracking
  - dropship order visibility
- `payments-accounting.spec.ts`
  - payment accounts readiness/activation
  - supplier bank approval/payout instruction
  - Xero mapping CRUD and precedence
  - ledger/refund entries visible
- `support-workflow.spec.ts`
  - RMA approve refund
  - approve require return -> return received -> restock/refund
  - deny path
- `ops-mission-control.spec.ts`
  - audit search
  - workflow runs
  - mission-control sections render with safe placeholders/links

### Supplier Portal Playwright

Add a new suite, likely under `src/Storefront/e2e-supplier` or a sibling project in Playwright config:

- supplier login/session forwarding if supported
- readiness view
- stock feed request/availability surface
- supplier change request for contact/bank details

## Dummy data implementation plan

Enhance `scripts/dev-dummy-data.sh` in layers:

1. Add deterministic helper primitives:
   - idempotency keys/reference ids
   - JSON request builders
   - `expect_2xx`, `allow_4xx`, and summary classification
   - lookup-by-name/code helpers
   - stable scenario names/codes
2. Add `--profile smoke|full|exhaustive|mirror-prod` with `full` remaining default.
3. Add a `--reset-scenario-data` option only if owning APIs support safe cleanup; otherwise seed idempotently by deterministic code/name/reference ids.
4. Seed scenario products/offers:
   - use admin catalog APIs for product creation/edit where available
   - create offers for every matrix row
   - seed inventory/location/carriers/supplier availability for matching products
5. Seed customers/accounts:
   - guest-like shopper
   - registered shopper with addresses
   - shopper with saved payment method via fake provider/setup path if available
6. Seed historical transactions by driving APIs:
   - cart -> checkout -> fake payment -> confirmation
   - RMA/ticket flows
   - subscription/usage admin/customer flows
   - package/tracking/admin flows
7. Emit fixture manifest:
   - `.run/dev-dummy-data/fixtures.json`
   - contains product ids/slugs/skus, customer emails, order ids, RMA ids, subscription ids, usage balances, supplier ids
   - Playwright reads this manifest for deterministic selectors instead of guessing first product.

## Regression command updates

- Keep `scripts/e2e-verify.sh` as the canonical full regression command.
- Add explicit coverage checklist entries when tests are added.
- Consider adding modes:
  - current fast automated check
  - `--live-smoke`
  - `--live-full`
  - `--live-exhaustive`
- Do not make normal CI depend on the exhaustive dummy profile until runtime is known.

## Ordered implementation tasks

| Task_ID | Task_Name | Phase | Status | Plan Path | Comments |
|---|---|---|---|---|---|
| qadata_1 | Coverage matrix and seed/test plan | P0 planning | done | `.ai-shared/plans/qa-and-dev-data-coverage-expansion.md` | This plan. |
| qadata_2 | Dummy data manifest + deterministic seed primitives | P1 data foundation | done | `.ai-shared/plans/qa-and-dev-data-coverage-expansion.md` | Added smoke/full/exhaustive profiles, stable scenario codes, fixture manifest, better step result classification, and dev-up dispatch. |
| qadata_3 | Product/supply/billing scenario seed data | P1 data foundation | done | `.ai-shared/plans/qa-and-dev-data-coverage-expansion.md` | Added API-first scenario product generation, offer creation, warehouse stock, fake carrier activation/defaulting, supplier availability, and fixture ids for all matrix codes. |
| qadata_4 | Historical commerce seed flows | P2 data scenarios | done | `.ai-shared/plans/qa-and-dev-data-coverage-expansion.md` | Added best-effort API-driven checkout/payment/status/support/RMA/shipment/package/subscription flows for representative scenario products and fixture ids. |
| qadata_5 | Backend integration coverage matrix | P3 backend tests | done | `.ai-shared/plans/qa-and-dev-data-coverage-expansion.md` | Added non-physical product matrix integration coverage for download/subscription/usage/manual-service entitlement side effects and updated regression checklist. |
| qadata_6 | Storefront Playwright scenario expansion | P4 storefront e2e | done | `.ai-shared/plans/qa-and-dev-data-coverage-expansion.md` | Added fixture-manifest helper and catalog scenario product Playwright coverage for visible product types plus private/unpublished search negative coverage. |
| qadata_7 | Admin Playwright scenario expansion | P4 admin e2e | done | `.ai-shared/plans/qa-and-dev-data-coverage-expansion.md` | Added admin operations Playwright coverage for catalog/offers/orders/commerce ops/payment accounts/supplier payouts/Xero mappings/mission control plus RMA action availability. |
| qadata_8 | Supplier Portal E2E coverage | P4 supplier e2e | done | `.ai-shared/plans/qa-and-dev-data-coverage-expansion.md` | Added Supplier Portal Playwright project/suite for sign-in redirect, readiness check, stock feed request, and supplier change request; dev-up/e2e-verify now launch SupplierPortal. |
| qadata_9 | Regression command and docs refresh | P5 validation/docs | done | `.ai-shared/plans/qa-and-dev-data-coverage-expansion.md` | Updated e2e-verify checklist/runtime launch, scripts README, getting-started/testing docs and HTML for smoke/full/exhaustive fixture data plus Storefront/Admin/SupplierPortal Playwright coverage. |
| qadata_10 | Full validation and runtime budget tuning | P6 validation | done | `.ai-shared/plans/qa-and-dev-data-coverage-expansion.md` | Automated regression passed; smoke/full/exhaustive live profile runs remain documented manual/nightly checks because they boot and mutate the full stack. |

## Validation commands

Run as slices land:

```bash
bash -n scripts/dev-dummy-data.sh
scripts/dev-up.sh --with-frontends --data smoke
scripts/dev-up.sh --with-frontends --data full
scripts/dev-up.sh --with-frontends --data exhaustive
cd src/Storefront && npm run lint && npx tsc --noEmit
cd src/Storefront && npm run test:e2e
cd src/Storefront && npx playwright test e2e-admin
scripts/e2e-verify.sh
scripts/e2e-verify.sh --live
```

Backend slices:

```bash
dotnet build 3commerce.sln
dotnet format --verify-no-changes
dotnet test 3commerce.sln
dotnet test tests/ --filter Category=Integration
```

## Acceptance criteria

- Dummy data can seed deterministic scenario products and historical states without direct DB writes.
- Playwright tests select stable fixture data from a manifest, not arbitrary first products.
- Storefront E2E covers all supported product/billing/supply classes at least once.
- Admin E2E covers major operator surfaces and state-changing paths.
- Backend integration tests prove cross-service side effects for physical, dropship, digital, subscription, and usage flows.
- `scripts/e2e-verify.sh` documents and runs the complete regression set at an appropriate default depth.
- Exhaustive coverage is available for nightly/local validation without making every developer loop prohibitively slow.

## Risks and mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| Combinatorial explosion | Slow/flaky CI | Use scenario matrix and pairwise/high-risk coverage, not every combination. |
| Dummy data becomes brittle | Playwright flakes | Stable scenario codes and generated fixture manifest. |
| API-first seeding lacks endpoints for some states | Gaps or temptation for DB writes | Add explicit dev/test endpoints only when justified, or cover state via integration tests. |
| Full profile is too slow | Developer friction | Keep smoke profile small; move exhaustive to nightly/manual. |
| Historical state conflicts on rerun | Non-idempotent seeds | deterministic references, idempotency keys, lookup-before-create patterns. |

## Confidence score

**0.78**

Reasoning: The platform has many owning APIs and existing tests, so broad coverage is feasible. The main uncertainty is whether every historical state can be created through public/admin APIs without new dev-only helpers; the plan preserves API-first seeding and pushes non-API state coverage into backend integration tests where appropriate.
