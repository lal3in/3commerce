# Platform services

The six DB-owning services added once CI was restored ([ADR-0030](../adr/0030-deferred-services-extracted.md)).
Each owns a Postgres database + named schema, sits behind the YARP gateway with internal-claims auth, and
uses the MassTransit EF outbox. See [services.html](./services.html) for every endpoint and the use cases
per option, and the [API contracts index](../api/api_contracts_index.md).

| Service | Gateway | Port | Owns |
|---|---|---|---|
| Marketing | `/api/marketing` | 5108 | Campaigns, short links (mt5_1/5_3); consent-aware **analytics collection** (anonymous `POST /events`, batch ≤50, deduped, coarse IP; admin `GET /admin/analytics/events`); **content publishing** (`/admin/content` draft → publish/schedule/rollback, anonymous `GET /content/{key}` for published content, signed expiring preview links; a Quartz job sweeps due scheduled publishes every minute) |
| Pricing | `/api/pricing` | 5109 | Prices + graduated tiers (mt7_1) |
| Audit | `/api/audit` | 5110 | Central searchable audit projection (mt6_1) — now fed by admin mutations across Catalog, Payments, Entity, Identity, Support, and Ordering; powers the Mission Control activity timeline |
| Workflow | `/api/workflow` | 5111 | Scheduled-job run history (mt6_3) |
| Entitlement | `/api/entitlement` | 5112 | Digital-line access; consumes `OrderConfirmed` (mt7_2/7_6) |
| Usage | `/api/usage` | 5113 | Metered balances + overage billing → Payments (mt7_4/7_5) |

Marketing and Pricing are standalone domains; Audit and Workflow are event projections; Entitlement and
Usage were extracted out of Fulfillment (which now ships only physical lines).

> **Not in the Pricing service: threshold promotions and coupon codes.** Promotions
> ([ADR-0051](../adr/0051-threshold-promotions-and-combinability.md)) and coupons
> ([ADR-0052](../adr/0052-coupon-codes-and-redemption-limits.md)) are owned by **Catalog**
> (`/api/catalog/admin/promotions`), because Catalog already owns the `Storefront` and `Offer` a
> promotion references and already projects into Ordering — whereas the Pricing service has no bus
> wiring and no path to checkout. Catalog publishes `PromotionChanged`; Ordering projects it into a
> local `PromotionCopy` and evaluates promotions there, so checkout never queries Catalog
> ([ADR-0008](../adr/0008-database-per-service-single-postgres.md)). Coupon **redemptions** are
> Ordering-owned (reserved at checkout, confirmed on order confirmation, released by the saga's
> cancellation path), surfaced to admin at `GET /api/ordering/admin/promotion-redemptions`. A
> promotion lowers the charged gross — no new ledger line, trial balance unchanged. The full chain
> (supplier cost → catalog price → offer price → promotions/coupons → storefront-wide discount → tax
> → shipping) is [Pricing & promotions](./pricing-and-promotions.md); every endpoint is listed in
> [services.html](./services.html) and the [API contracts index](../api/api_contracts_index.md).

> **Buyer identity at checkout (ADR-0063 P3).** `POST /api/ordering/checkout` accepts an optional
> `billingAddress` (absent = bill to the shipping address, which is then stored as the bill-to) and an
> optional `buyer` block — company name, tax ID and its numeric `taxIdScheme` (the shared `TaxIdScheme`:
> EU VAT, ABN, UK VRN, NZ GST, CA BN, CH UID, US state sales-tax ID, …). Ordering checks the tax ID's
> **format** per scheme with the same rules Entity uses for sellers (mirrored, pinned by a parity test; no
> VIES lookup yet) and refuses a bad block with a 400 problem+json naming the field (`checkoutBlock` 17,
> `field` = `buyer.taxId` …). Both are stored on the checkout attempt and the order and returned on the
> order detail. They **never change the amount charged** — reverse charge is phase 2 — and they are
> printed on the invoice once the Invoicing service exists. The storefront checkout shows them as the
> "Billing address" and "Business purchase" sections; the account's order history shows them back.

For audience-specific positioning of these capabilities — shoppers, tenants, suppliers, admins, technical evaluators, and finance/compliance reviewers — see [Selling information](./selling-information.md).
