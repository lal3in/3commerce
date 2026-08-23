# 0047 — Storefront-scoped, active-window Offer price (authoritative at checkout and on the storefront)

Status: Accepted
Area: Catalog / Ordering / pricing
Extends: [0028](./0028-product-supply-profiles-composable-supply.md) (Offers own price), [0038](./0038-per-currency-shelf-prices-and-tax-entry.md) (per-currency shelf price on the Variant), [0040](./0040-per-currency-per-storefront-ledger.md) (per-currency, per-storefront ledger), [0008](./0008-database-per-service-single-postgres.md) (read-copy projections, no cross-service query)

## Context

ADR-0028 made the **Offer** the price owner going forward, but two prices still coexisted with no
rule that connected them to what a shopper actually paid:

- **The Variant shelf price** (`Variant.PriceMinor` + per-currency `VariantPrice`, ADR-0038) drove
  storefront display and checkout revalidation. It is a *catalog* price, not scoped to a store or a
  time window.
- **`Offer.PriceMinor`** (ADR-0028) existed on the supply aggregate, but `OfferChanged` did **not**
  project it, and checkout charged the catalog `ProductCopy.SellingPriceMinor` / `VariantPriceCopy`.
  So an operator could set an offer price and nothing charged it.

This blocked the ordinary retail need to run a **per-store, time-boxed price** — a launch price on the
AU storefront for December only, a different EUR price on the EU store — without rewriting the variant's
base shelf price (which is shared across every store of that currency). And because a storefront's
currency is 1:1 (`Storefront.Currency`, ADR-0038), "per currency + per storefront" collapses to
**per storefront**.

The hard requirement that makes this safe: **what the storefront shows must equal what checkout charges.**
A price shown on the product page or listing that then differs at checkout is both a trust failure and,
in inclusive-tax regimes (ADR-0038), a compliance failure.

## Decision

1. **The Offer gains a storefront scope and an active window.** `Offer` adds nullable `StorefrontId`
   (null = every storefront of the offer's `Currency`) and nullable `ActiveFrom` / `ActiveUntil` (UTC,
   inclusive; null bounds are open-ended). `Offer.IsEffectiveAt(now, storefrontId)` is true when the
   offer is `Active`, targets that storefront (or all), and `now` is within the window. Currency matching
   is the caller's responsibility (storefront currency is 1:1). Migrations land in **both** Catalog
   (`Offer`) and Ordering (`OfferCopy`).

2. **`OfferChanged` projects the price + scope + window** (appended fields, back-compatible defaults:
   `PriceMinor = 0`, `StorefrontId = null`, open-ended window) into Ordering's local `OfferCopy`
   (ADR-0008 read copy — no cross-service query at checkout). A copy from before these fields carries
   `PriceMinor 0`, which means "no offer price → keep the catalog price".

3. **An effective offer's price is the authoritative charge (shown == charged).** For a line on a given
   storefront + currency at `now`, `OfferResolution.ResolvePricingOffer` picks the offer whose price is
   charged: same grain precedence as fulfilment resolution — a **variant-specific** offer beats a
   product-level one, then a **storefront-scoped** offer beats an all-storefront one, then lowest
   `Priority` wins — filtered to offers that are effective and carry a non-zero `PriceMinor`. When one
   applies, `CheckoutEndpoints` uses it as the line's unit price.

4. **The same resolution drives the storefront display.** Catalog's public product detail
   (`GetBySlug`) and the search/listing endpoint (`Search`, product-level offers only for the min price)
   apply the *identical* effective-offer rule, so the shopper sees the offer price they will be charged.

5. **The offer price does not trip the price-drift 409.** Checkout revalidates the catalog price against
   the cart (the "review your cart" 409 on drift, ADR pre-existing). Because the offer price is exactly
   what the storefront showed, an offer-overridden line is charged the offer price **without** a 409;
   only a genuine *catalog* drift on a line with no effective offer still trips it.

6. **The Offer price is a shelf/sell price, distinct from supplier cost.** `Offer.PriceMinor` is what the
   shopper pays; `Offer.SupplierCostMinor` (COGS, ADR-0028) is what the platform pays the supplier. This
   ADR governs only the sell price; cost is unchanged.

## Alternatives considered

- **Per-currency price rows on the Variant with a window (extend ADR-0038).** Rejected: it puts a
  supply/merchandising concern (a supplier's time-boxed store price) onto the catalog variant and can't
  express *which supplier's* offer the price belongs to. The Offer already owns supplier + price grain.
- **A separate promotions/price-rule engine.** Out of scope for this epic — a full campaign engine is far
  more than a per-store scheduled price. The Offer window covers the concrete need without a new service.
- **Show the catalog price, charge the offer price (fix silently at checkout).** Rejected outright — it
  is the exact shown≠charged failure this ADR exists to prevent.

## Consequences

- Operators can run per-store, scheduled prices from the existing `/offers` admin surface (storefront
  scope + active-from/until on the Offer form) with no change to the variant's base shelf price.
- Checkout loads the `OfferCopy` read model **once** up front (already loaded for the shipping gate) and
  reuses it for the price override, so there is no extra cross-service call.
- A storefront-scoped offer **must** carry that storefront's currency for its price to apply there
  (1:1 currency); an offer in the wrong currency simply never matches.
- Reporting is unaffected: the charged amount flows through `CheckoutAttempt`/`Order` as before; the
  offer price is just the source of the unit price. The per-storefront ledger (ADR-0040/0045) books the
  charged amount verbatim.
- **Approval gating is layered on top by [0048](./0048-supplier-approval-gated-offer-availability.md):**
  an offer from an unapproved supplier must not set a price or availability even when otherwise effective.
