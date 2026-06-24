# ADR-0028: Product supply profiles (Offers) — composable supply, fulfilment, pricing, entitlement

## Status

Accepted

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
