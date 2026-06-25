# Feature: Multi-Tenant Platform Expansion — Phase 7 Digital Supply & Billing

Parent plan: `.ai-shared/plans/multi-tenant-platform-expansion.md`
Decision record: `docs/adr/0028-product-supply-profiles-composable-supply.md`

## Feature Description

Extend the composable supply model (ADR-0028, seam laid in mt4_1b) along the **digital axis**:
digital one-time (download/license), **subscription / recurring access**, and **metered usage**
(tokens, transactions, API calls, seats, minutes, storage). Add the **Entitlement** and **Usage
Metering** services, a **Pricing/Billing** boundary (one-time + subscription + usage + tiered),
and the storefront/checkout affordances for mixed carts.

## User Story

As a tenant selling digital and AI/SaaS products
I want to offer subscriptions, usage/token plans, and downloads alongside physical goods
So that a single storefront and order can mix one-time, recurring, and metered supply.

## Problem Statement

The platform is physical-only end to end: price is one-time, checkout authorizes one gross and
posts one balanced sale, and there is no entitlement, subscription, or usage concept. Digital
products need access lifecycle, recurring billing, and consumption metering — none of which fit
the single-capture money flow.

## Solution Statement

Build digital supply behind the `ISupplyStrategy` seam: Pricing/Billing owns price models;
Entitlement owns what the customer receives; Usage Metering owns consumption. The order line's
`billing_mode` (laid in mt4_1b) lets the confirmation saga fan out to issue entitlements, start
subscriptions, or open usage meters, while one-time lines keep the existing auth/capture path.

## Feature Metadata

**Feature Type**: New Capability
**Estimated Complexity**: Very High
**Primary Systems Affected**: new Pricing/Billing, new Entitlement, new Usage Metering, Ordering, Payments, Catalog, Entity, Storefront, Gateway, Audit
**Dependencies**: mt4_1b supply seam (Offers, `fulfilment_type`, `billing_mode`, price-off-variant); Phase 1 tenant/RLS/PDP; Payments `IPaymentProvider`.

---

## CONTEXT REFERENCES

### Relevant Codebase Files — READ BEFORE IMPLEMENTING

- `src/Services/Payments/Domain/*` + `IPaymentProvider` — extend for recurring + usage charges.
- `src/Services/Ordering/Domain/Order.cs` / `CheckoutAttempt.cs` — `billing_mode` line fan-out.
- `src/Services/Ordering/Domain/Pricing.cs` — current one-time `PricingEngine` (to generalize).
- `src/Services/Catalog/Domain/Product.cs` — `product_type`, offers, price ownership.
- `docs/adr/0028-product-supply-profiles-composable-supply.md`.

### New Services / Files

- **Pricing/Billing service** (`pricing_db`): `prices` (pricing_model, billing_period, currency), `price_tiers`.
- **Entitlement service** (`entitlement_db`): `customer_entitlements`, `subscription_products`.
- **Usage Metering service** (`usage_db`): `usage_plans`, `usage_records`, `usage_balances`.
- Storefront React: subscription/usage/download checkout + account "my access" surfaces.

### Patterns to Follow

- One `ISupplyStrategy` per fulfilment_type; table-per-type detail (ADR-0028).
- Recurring + usage charges behind `IPaymentProvider` (Stripe Billing/usage records), Fake first.
- Tenant-scoped throughout (ADR-0023); audit sensitive entitlement/usage events (mt6_1/mt6_2).
- Usage rolls up into `usage_balances` periods — never recompute millions of records per read.

---

## TASKS

### mt7_1 ADD Pricing/Billing service + price models

- **IMPLEMENT**: `prices` (`pricing_model = one_time | subscription | usage_based | tiered`,
  `billing_period = once | monthly | yearly`, currency, amount) + `price_tiers` (from/to qty,
  unit price) owned by a Pricing/Billing service. Finish moving price off `Variant.PriceMinor`
  onto the Offer (mt4_1b introduced one_time). Ordering/checkout resolve price from the offer's
  price model.
- **VALIDATE**: `dotnet test src/Services/Pricing/tests --filter Pricing`.

### mt7_2 ADD Entitlement service + subscription products

- **IMPLEMENT**: `customer_entitlements` (`entitlement_type = subscription | license | download |
  api_access | service_access`, status `active | expired | suspended | cancelled`, starts/expires)
  + `subscription_products` (billing_period, access_level, trial_days, auto_renew). Issue an
  entitlement when a digital line confirms (`EntitlementCreated → CustomerAccessActivated`).
- **VALIDATE**: `dotnet test src/Services/Entitlement/tests --filter Entitlement`.

### mt7_3 ADD subscription billing (recurring)

- **IMPLEMENT**: Recurring charge behind `IPaymentProvider` (Stripe subscriptions; Fake for
  tests). `billing_mode = recurring` lines set up a subscription at checkout instead of a single
  capture; renewals, trials, dunning, cancel/suspend reflected on the entitlement.
- **VALIDATE**: `dotnet test src/Services/Payments/tests --filter Subscription`.

### mt7_4 ADD Usage Metering service

- **IMPLEMENT**: `usage_plans` (`meter_type = transaction | token | request | minute | seat |
  storage_gb`, included_quantity, reset_period, overage_allowed/price) + `usage_records`
  (append-only) rolled into `usage_balances` (period included/used/remaining). `UsageRecorded →
  UsageBalanceUpdated`.
- **VALIDATE**: `dotnet test src/Services/Usage/tests --filter Usage`.

### mt7_5 ADD usage-based billing + overage

- **IMPLEMENT**: Rate usage against the plan/tiers; `UsageThresholdReached → OverageCharged /
  AccessLimited`. Metered charges via `IPaymentProvider` usage records; balances gate access.
- **VALIDATE**: `dotnet test src/Services/Usage/tests --filter Overage`.

### mt7_6 ADD digital fulfilment flows + storefront UX

- **IMPLEMENT**: Digital fan-out in the confirmation saga (download/license issue, entitlement
  activation, meter open). React storefront: subscription (interval/trial), usage (plan + included
  + overage), and download affordances; account "my access / usage" pages; mixed-cart checkout
  (one-time + recurring + metered in one order).
- **VALIDATE**: storefront `tsc` + Playwright digital-checkout spec.

### mt7_7 UPDATE docs / e2e-verify / ADR currency for digital supply

- **IMPLEMENT**: OpenAPI for new services, `docs/help`, `e2e-verify.sh` ladder additions, ADR
  follow-ups; ensure contracts/regression stay current (mt6_12 discipline).
- **VALIDATE**: `scripts/e2e-verify.sh` checklist updated; full suite green.

## Acceptance

- [ ] A product can be sold as physical, digital-download, subscription, or metered via Offers.
- [ ] One cart/order can mix one-time, recurring, and metered lines and bill each correctly.
- [ ] Entitlements gate digital access; usage balances gate metered access with overage handling.
- [ ] Price is owned by the Offer with a pricing_model; `Variant.PriceMinor` is retired.
