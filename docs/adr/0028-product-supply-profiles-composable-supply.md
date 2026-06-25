# ADR-0028: Product supply profiles (Offers) — composable supply, fulfilment, pricing, entitlement

## Status

Accepted — implemented through Phase 7 (see the Phase 7 implementation note below).

## Context

The platform must sell products that are **sourced and delivered in fundamentally different
ways**: physical from an own/partner warehouse, physical via dropship, digital one-time
(download/license), subscription / recurring access, and metered usage (tokens, transactions,
API calls, seats, minutes). The same product can be offered by **multiple suppliers** and
listed across **multiple storefronts at different prices**.

What ships today bakes in the opposite assumptions:

- Supply is a physical-only `CatalogFulfillmentSource` enum **on the catalog variant**,
  re-declared as a second `FulfillmentSource {Unassigned, Dropship, OwnWarehouse}` enum in
  Ordering, and flattened to a bare **string** on the event bus (`OrderLineInfo`).
- Price is a single `Variant.PriceMinor` (one-time only).
- Supplier is a single `Product.SupplierRef` **string** (one supplier per product).
- `Entity.Supplier` has no physical/digital classification.
- Stock lives in **two** places: `Catalog.Variant.StockQuantity` and `Fulfillment.InventoryItem`.

These conflate *what is sold* with *how it is sourced, delivered, and charged*, and block the
multi-supplier / digital / recurring roadmap (ADR-0002 anticipated physical third-party
catalogs only).

## Decision

Adopt a **composable supply model** with a first-class Offer.

- **Product = merchandising only** (what is sold), supply-agnostic. Add `product_type`
  describing *nature* — `physical | digital | service | bundle` — **not** the fulfilment
  mechanism. "Subscription" is never a `product_type`; it is a fulfilment + pricing model.
- **Offer (product supply profile)** is first-class: `(tenant, product/variant, supplier) →
  supply_category + fulfilment_type`, owning **price, availability, and delivery strategy**.
  A product/variant may have many offers (multi-supplier); a storefront-product selects one.
- **One `ISupplyStrategy` seam** (availability / reserve / fulfil / price) with **table-per
  fulfilment-type** detail (`warehouse`, `dropship`, `digital_download`, `subscription`,
  `usage`). Adding a supply mode = a new strategy + one detail table; existing modes are
  untouched (open/closed). JSONB is reserved only for supplier-specific attributes, not for
  the strategy itself.
- **Price moves off the variant onto the offer**, gaining a `pricing_model`
  (`one_time | subscription | usage_based | tiered`) and tier rows.
- **Order line carries `fulfilment_type` + supplier/offer ref + `billing_mode`**
  (`one_time | recurring | metered`); the confirmation saga fans out per type: ship · forward
  to supplier · issue entitlement · start subscription · open a usage meter.
- **Service ownership**: Catalog = products/merchandising; Entity = suppliers (+ `supplier_type`);
  Fulfillment = inventory, reservations, **inventory movements**, shipments, dropship orders;
  **Entitlement** (new) = digital access / subscriptions / licenses; **Usage Metering** (new) =
  meters / records / balances; **Pricing/Billing** = prices, models, tiers, recurring + usage
  charging. Stock has a **single owner (Fulfillment)**; Catalog only projects availability.
- Tenant-scoped throughout (ADR-0023). This **generalizes ADR-0003** (per-line fulfilment
  source) from a physical-only enum into a typed supply profile.

## Alternatives considered

- **Keep supply on the catalog variant (status quo)** — cannot represent multi-supplier,
  digital, or recurring; forces a hostile cross-service contract migration later.
- **Single JSONB blob per product for supply config** — flexible but loses DB constraints and
  queryability; we keep table-per-type and use JSONB only for open supplier attributes.
- **Subclass per product type** (`WarehouseProduct`, `TokenProduct`, …) — rigid class
  explosion; the composable Offer + strategy avoids it.

## Consequences

- A foundational **supply-seam** task lands before reservations: introduce the Offer, add
  `supplier_type`, widen `fulfilment_type`, and move price off the variant.
- **Reservations key off the Offer**, an **`inventory_movements`** ledger is added (a
  reservation is a movement; it also seeds audit), and the **duplicate stock** is collapsed to
  Fulfillment.
- Two new services (**Entitlement**, **Usage Metering**) and a **Pricing/Billing** boundary
  become a dedicated phase (digital supply & billing).
- The single-capture money flow must accept **mixed carts** (one-time + recurring + metered);
  the order line gains `billing_mode`.
- `mt4_1` `InventoryItem` is unchanged — it becomes the **warehouse strategy** under the seam.
- New supply/fulfilment/pricing/meter enums must stay deliberately small and composable
  (ADR principle): nature on the product, mechanism on the offer.

## Phase 7 implementation note (2026-06, capability-first)

Phase 7 (digital supply & billing) was delivered **capability-first**: the domain behaviour landed
on **existing service boundaries** rather than scaffolding the three new DB-owning services the
context above anticipates. This was a deliberate call while CI was quota-blocked — a new service's
compose/Helm/CI/migrator wiring cannot be validated without the pipeline, whereas domain + service +
Testcontainers integration are fully verifiable locally. The composable-supply model is unchanged; only
the *physical packaging* of the new capabilities is deferred. Where each capability lives today:

- **Pricing / price models → the Offer (Catalog).** The Offer is the price owner (`PricingModel`,
  `PriceMinor`, `BillingPeriod`, graduated `OfferPriceTier`s, `PriceFor(qty)`). A dedicated
  Pricing/Billing service is deferred until recurring/usage *charging* needs it — and that charging is
  Payments territory (below). `Variant.PriceMinor` retirement is likewise deferred.
- **Entitlements + usage metering → Fulfillment.** Fulfillment already owns the per-line confirm
  fan-out, so a digital/service line issues an **`Entitlement`** instead of a shipment, and metered
  usage (`UsageBalance` kept incrementally + append-only `UsageRecord`) lives beside it. The
  dedicated Entitlement and Usage Metering services migrate out of Fulfillment when CI returns.
- **Subscription + overage billing → Payments.** Payments owns the rail/ledger, so the
  **`Subscription`** lifecycle (renew/cancel behind `IPaymentProvider`) and the usage **overage
  charge** (`UsageOverageCharge` consumer) live there.

Seams that make the future extraction mechanical: the cross-service triggers are already events
(`OfferChanged` carries the price model; `SubscriptionRequested` and `UsageOverageCharge` are
contracts), and the new aggregates are self-contained — moving them is a project move + a DbContext,
not a redesign. Deferred refinements tracked in the plan: the dedicated services, customer-facing
`/me/*` digital surfaces, ledger journals for recurring/overage charges (they already hit the rail),
trials/auto-renew and period-close schedulers, and Payments→Fulfillment entitlement-status reflection.
