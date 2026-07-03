# 0038 — Per-currency shelf prices on the Variant, and the tax-entry convention

Status: Accepted (2026-07-04)
Context: multi-storefront currency/tax (PRs #39–#41), project-review remediation rev_6/rev_10.

## Context

Tenants sell through per-region storefronts (AU/EU/US…), each with a configured currency and tax
regime (`Storefront.Currency`, `TaxRegime`, `TaxRateBasisPoints`). Two price systems coexisted with
no reconciling decision:

- **`VariantPrice`** (PR #40): tenant-authored explicit price per currency per variant — no FX, no
  derivation. Drives storefront display, cart pricing, and checkout revalidation. A product with no
  price in a storefront's currency is hidden there.
- **`Offer`** (ADR-0028): the supply-side aggregate — supplier, supply category, fulfilment type,
  pricing model (flat/tiered/subscription/usage), billing period. mt7_1 carried a follow-up to
  "retire `Variant.PriceMinor`".

Separately, checkout charged storefront tax **exclusively (added on top) in every regime**, but AU
GST and EU VAT are legally tax-**inclusive display** markets: the shelf price must be what the
shopper pays.

## Decision

1. **The Variant owns the shopper-facing shelf price, per currency.** `VariantPrice(Currency,
   PriceMinor)` rows are the tenant's explicit list prices; `Variant.PriceMinor`/`Currency` remain
   as the base/default (and the fallback when a currency has no explicit row). The **Offer keeps
   supply/billing pricing** (supplier cost basis, pricing models, tiers, billing periods) per
   ADR-0028 — it is not a display price. The mt7_1 "retire `Variant.PriceMinor`" follow-up is
   **resolved as: keep it**, re-scoped from "the" price to "the base shelf price".

2. **Tax-entry convention — prices are entered as the final shelf price for that currency:**
   - **Inclusive regimes (`AuGst`, `EuVat`)**: the entered price **includes** tax. The shopper pays
     exactly the listed price; checkout reports the contained tax informationally
     (`tax = round(amount × bps / (10000 + bps))`, AwayFromZero) and does **not** add it.
   - **Exclusive regimes (`UsSalesTax`, `None`, `Other`)**: the entered price **excludes** tax;
     checkout adds `round(amount × bps / 10000)` on goods + shipping, as before.
   - The regime lives on the **storefront**; the flag reaches Ordering as `TaxInclusive` on the
     `StorefrontConfigChanged` projection (ADR-0008 read copy — no cross-service query), resolved at
     checkout by the cart's currency exactly like the rate.

3. **Operator visibility is part of the contract**: every admin price-entry field carries a
   tooltip/note stating the convention (Catalog product editor base + per-currency price inputs;
   Commerce Ops storefront tax fields), so a tenant admin always knows whether the number they type
   includes tax.

## Consequences

- AU/EU shoppers pay the listed price to the cent; the checkout "Includes GST/VAT" line is
  informational. US behavior is unchanged (tax added at checkout).
- A tenant selling one currency through storefronts with **different regimes** (e.g. EUR in both an
  `EuVat` and a `None` storefront) would need distinct prices per regime — not supported; one
  currency carries one shelf-price meaning. Revisit trigger: a real tenant needs regime-split
  pricing within a currency (would become a per-storefront `PricesIncludeTax` override).
- Ledger/reporting: `CheckoutAttempt.TaxMinor`/`Order.TaxMinor` hold the charged-or-contained tax
  either way; `GrossMinor` is always what the shopper pays. Payments charges the tax-inclusive net
  verbatim (rev_2 — it never applies its own tax).
- Supersedes the single-store-currency framing (`Store:Currency`, BL-9) for storefront display;
  that config remains only as the empty-cart/default fallback.
