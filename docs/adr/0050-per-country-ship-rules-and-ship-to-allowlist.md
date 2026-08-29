# 0050 — Per-country ship rules and the storefront ship-to allowlist

Status: Accepted
Area: Catalog / Ordering / commerce
Extends: [0008](./0008-database-per-service-single-postgres.md) (read-copy projections, no cross-service reads), [0016](./0016-global-shipping-dap-tax-at-home.md) (global = ship worldwide DAP), [0038](./0038-per-currency-shelf-prices-and-tax-entry.md) (per-currency shelf prices + tax-entry convention), [0042](./0042-physical-shipping-carriers-and-product-type-policy.md) (shipping charged only for shippable carts)

## Context

The platform shipped "global = ship worldwide, tax presence at home only" (ADR-0016) and a per-currency,
regime-aware tax convention (ADR-0038). Two destination-aware gaps remained:

- **Where a storefront ships.** A store had no way to say "I only ship to these countries". Every
  destination reached checkout and was charged, even ones the operator can't fulfil to.
- **How tax/shipping vary per product per destination.** Some products carry destination-specific
  logistics or tax treatment — a courier already covers shipping to a country, or a product is not
  subject to that destination's tax — with no way to express it. Tax was a single storefront rate applied
  to every line.

Checkout runs in **Ordering**, which owns no Catalog data directly (ADR-0008). Any destination rule an
operator sets in Catalog therefore has to reach Ordering as **projected read state**, not a synchronous
query, and the money math has to stay integer-minor-unit and trial-balanced (ADR-0038/0045).

## Decision

Two independent, tenant-scoped layers, both authored in Catalog and applied at checkout in Ordering from
local read copies (ADR-0008):

1. **Ship-to allowlist — storefront-wide, all-or-nothing.** `Storefront.ShipToCountries` is a list of
   ISO 3166-1 alpha-2 codes; **empty = ships worldwide** (no restriction). It rides
   **`StorefrontConfigChanged`** into Ordering's **`StorefrontTaxCopy.ShipToCountries`**. Checkout, before
   authorizing any payment, **rejects** an order whose ship-to country is outside a non-empty allowlist
   (*"This storefront does not ship to XX."*); the storefront checkout country picker is limited to served
   countries. Codes are validated (2-letter A–Z), upper-cased and de-duplicated; a null argument leaves
   the list untouched (an older admin client can't silently wipe it).

2. **Per-product ship rules — finer-grained tax/shipping.** `Product.ShipRules` is a list of
   **`ProductShipRule(CountryCode, ChargeDestinationTax, ShippingCovered)`**, each targeting one
   destination — an **ISO-2 country** or the **`*` whole-world default**. They ride **`ProductUpserted`**
   (optional field) into Ordering's **`ProductCopy.ShipRules`**. At checkout:
   - **`ChargeDestinationTax = false`** excludes that product's line from the **tax base** (the shopper
     still pays the full line — only the tax portion shrinks).
   - **`ShippingCovered = true`** means shipping is already covered for that product to that country; when
     **every** line in the cart resolves to a covered rule, the order's shipping is **waived** to 0
     (all-or-nothing — order-flat shipping can't be partially attributed).
   - **Precedence (`RuleFor`)**: a specific-country rule wins over the `*` default; no matching rule ⇒
     normal tax + shipping. Codes are validated the same way; **null** on the wire means "no overrides
     carried" (leave existing rules as-is — so a CSV re-import that omits the field can't wipe them), an
     **empty list** clears them.

3. **Mandatory-rules feature switch.** `TenantCatalogSettings.RequireProductShipRules`
   (`GET/PUT /api/catalog/admin/settings`) makes **≥1 rule mandatory** at product create/update. When ON,
   a write that would leave the product with **no** rules returns **400** (`ValidationProblem`, key
   `ShipRules`); a write that omits the field on a product that already has rules keeps them and passes
   (the gate fires only when the product would end up with none). A malformed country code returns a 400
   `ValidationProblem`, not a 500.

4. **Country/region-aware forms end-to-end.** Country is a full ISO 3166 dropdown (Admin `CountrySelect`
   over a 249-country `Countries` vocabulary; storefront `src/Storefront/lib/countries.ts`), and the
   region field's **label** adapts to the selected country (AU→State, CA→Province, UK→County,
   JP→Prefecture, …) via `RegionLabel`/`regionLabel()`. **Region** is persisted end-to-end —
   `Order.ShipRegion` + `CheckoutAttempt.ShipRegion` (and `ShipToInfo` so Fulfillment gets it for labels)
   and Identity `Address.Region`.

The tax/shipping inputs are **recomputed**, but the money stays integer-minor-unit and **trial-balanced**:
the payment-intent total is unchanged; only the tax base and shipping charge are affected, and a fully
tax-exempt cart yields tax = 0 while the shopper still pays goods + shipping (net 0). Shipping follows the
goods' taxability — when no goods are destination-taxable, shipping is not destination-taxed either.

## Alternatives considered

- **One combined "destination policy" object.** Rejected: the allowlist is a storefront-level *fulfilment*
  guardrail (can I ship here at all?) while ship rules are per-product *tax/shipping* modifiers. They have
  different owners, cardinalities, and events; conflating them would couple a store-wide ship decision to
  per-product edits.
- **Compute tax/shipping in Catalog and send Ordering a final number.** Rejected: checkout is the single
  charge authority (ADR-0038); Ordering must resolve tax/shipping against the live cart, currency, and
  storefront tax copy. Catalog only supplies the *rules*, projected.
- **Let ship rules widen the allowlist.** Rejected: rules are **advisory to tax/shipping only** and never
  grant reachability — a destination must still pass the allowlist guard first.
- **Store region as free text with a fixed "State" label.** Rejected: the region label is
  country-dependent (Province/County/Prefecture…), and a country dropdown avoids invalid 2-char free-text.

## Consequences

- `Storefront` gains `ShipToCountries`; `Product` gains `ShipRules`; a new `TenantCatalogSettings`
  aggregate carries the mandatory-rules switch — all persisted as **jsonb** with explicit
  `ValueComparer<List<record>>` in both `CatalogDbContext` and `OrderingDbContext` (structural change
  tracking). `Order` + `CheckoutAttempt` gain `ShipRegion`; Identity `Address` gains `Region`.
- Two contracts carry the new state: `StorefrontConfigChanged.ShipToCountries` and
  `ProductUpserted.ShipRules` (both **optional / back-compatible** — a publisher predating the field sends
  null, treated as "no overrides / no restriction"). Projected into `StorefrontTaxCopy` and `ProductCopy`;
  no synchronous cross-service reads (ADR-0008).
- Checkout gains a pre-payment allowlist guard and a rule-aware tax base + shipping waiver; a fully-exempt
  cart posts tax = 0 with a trial-balanced ledger (ADR-0045).
- Admin surfaces: the **Catalog** editor toggles the mandatory switch and edits per-product rule rows; the
  **Commerce ops** storefront Manage form edits the ship-to allowlist. See
  [Ship-to countries & per-country ship rules](../help/ship-to-country-rules.md) and
  [Admin operations](../help/admin-operations.md).
