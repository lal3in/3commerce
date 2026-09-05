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

For audience-specific positioning of these capabilities — shoppers, tenants, suppliers, admins, technical evaluators, and finance/compliance reviewers — see [Selling information](./selling-information.md).
