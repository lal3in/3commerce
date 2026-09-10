# 0053 — Storefront-wide items discount: a store setting, not a promotion

Status: Accepted — implemented (shipped as PR #256, recorded here retrospectively; ADR-0051/0052 already build on it)
Area: Catalog / Ordering / pricing
Operator/handbook view: [`docs/help/pricing-and-promotions.md`](../help/pricing-and-promotions.md) §2.6 — the whole money chain (supplier cost → catalog price → offer price → promotions/coupons → storefront-wide discount → tax → shipping) with a worked example
Extends: [0038](./0038-per-currency-shelf-prices-and-tax-entry.md) (inclusive vs exclusive tax, now computed on the discounted base), [0047](./0047-storefront-scoped-active-window-offer-price.md) (the offer-resolved effective selling price the percentage rides on), [0008](./0008-database-per-service-single-postgres.md) (read-copy projection; checkout never queries Catalog), [0045](./0045-mandatory-per-storefront-ledger-attribution.md) (no new ledger line — the charged gross simply drops)
Refined by: [0051](./0051-threshold-promotions-and-combinability.md) (promotions arrive alongside it; the taxable-discount allocation becomes exact per line), [0052](./0052-coupon-codes-and-redemption-limits.md)

## Context

A tenant running a store-wide sale ("10% off everything this week") had no way to say so. The only
levers were the per-currency shelf price on each `ProductVariant` (ADR-0038) and the per-offer price
(ADR-0047) — both **per product**, both requiring a bulk edit to raise and a second bulk edit to undo,
and neither showing the shopper *why* the price dropped. There were no promotions at all at this point:
`Pricing.cs` carried a dormant engine no production caller reached (see ADR-0051 context).

Three facts shaped the decision.

1. **The requirement is a store setting, not a merchandising rule.** It has no threshold, no window, no
   scope, no eligibility and no code — it is simply "this store currently sells at X% off". Everything a
   promotion needs to be a promotion is absent.

2. **Checkout must not query Catalog** (ADR-0008). `Storefront` is a Catalog aggregate; the percentage
   has to reach Ordering as a projected read copy, or checkout cannot charge it.

3. **Whatever changes the goods' price changes the tax base.** Both ADR-0038 regimes (inclusive AU GST /
   EU VAT, exclusive US sales tax) derive tax from the goods value, so a discount that does not flow into
   the tax computation charges the wrong tax in every store.

## Decision

1. **A percentage on the `Storefront` aggregate, stored as basis points.**
   `Storefront.DiscountBasisPoints` (int, **0–10000; 0 = none**, default 0), set through
   `SetDiscount(int? bps, now)` which rejects anything outside the range with a `CatalogRuleException`.
   Basis points rather than a decimal percent for the same reason `TaxRateBasisPoints` is: it is an exact
   integer, it needs no currency-decimal story, and the two fields then read alike. **`null` leaves the
   current value untouched**, so an older admin client that doesn't know about the field cannot silently
   wipe a live sale — the same defensive shape `SetDefaultLanguage` uses. `DuplicateFrom` clones it, so a
   duplicated storefront inherits the sale.

2. **The Commerce ops admin page is the only surface.** It takes a **percent** (0–100, step 0.01) and
   converts to basis points on save (`PercentToBps`, `MidpointRounding.AwayFromZero`); the storefront
   table gains a Discount column rendering `—` when 0. Operators think in percent; the wire and the
   database stay in basis points.

3. **It discounts the ITEMS' subtotal only.** Never shipping, never fees. The base is
   `Σ resolved unit price × quantity` — the *same* base a promotion measures (ADR-0051 decision 5) — so
   the percentage rides on whatever the shopper is actually charged per line: the effective `Offer` price
   when one applies (ADR-0047, approval-gated by ADR-0048), otherwise the catalog shelf price. A 10%
   discount over a $120 offer subtotal takes $12, not 10% of the $200 catalog price.

4. **It is a setting, so it is ALWAYS applied — the promotion `Combinable` flag does not govern it.**
   Combinability is a promotion-versus-promotion rule (ADR-0051 decision 8). The storefront-wide discount
   is outside that contest: it stacks **additively** on top of whatever the promotion engine decided, and
   the two are **jointly capped at the subtotal**
   (`Math.Clamp(promotionDiscount + storefrontDiscount, 0, subtotal)`) so the goods can never be
   discounted below zero. An operator who wants a store-wide sale that *competes* with promotions creates
   a storefront-scoped promotion instead; that is what promotions are for.

5. **Its position in the pricing order is after the price and after the promotion decision.**

```
offer/catalog price → promotions/coupons → storefront-wide discount → tax on the discounted base → shipping
```

   Concretely, inside `PricingEngine.Price` and `CheckoutEndpoints.Checkout`:

```
subtotal            = Σ resolved unit price × qty                      [items only, excl. tax/ship/fees]
promotionDiscount   = PromotionEvaluator outcome                        (ADR-0051; 0 before it existed)
storefrontDiscount  = round(subtotal × DiscountBasisPoints / 10000)     [AwayFromZero — ties favour the shopper]
discountMinor       = min(promotionDiscount + storefrontDiscount, subtotal)
taxBase             = taxableSubtotal − taxableDiscount + taxableShipping
tax                 = inclusive ? round(taxBase × bps / (10000+bps)) : round(taxBase × bps / 10000)
chargeBase          = subtotal − discountMinor + shippingMinor
```

   **Why after promotions.** A promotion's threshold must be measured against the price the shopper sees
   for *that product*, not against a store-wide sale price — otherwise every storefront running a sale
   would quietly move its own "spend $100" bar. Measuring first and discounting after keeps the two
   concepts independent: the store setting cannot change which promotion wins, and no promotion can turn
   the store setting off.

6. **Tax is recomputed on the discounted base, in both regimes.** The discount reduces the taxable base
   before the inclusive/exclusive formula runs, so an AU/EU store still charges exactly the listed
   (discounted) amount with the contained GST/VAT reported informationally, and a US store adds sales tax
   on the discounted goods. The taxable share of the discount excludes ship-rule-exempt lines
   (`chargeDestinationTax = false`, ADR-0050). **Shipping is outside the discount base entirely** and is
   taxed on its own terms.

7. **Projected Catalog → Ordering, mirroring the offer/promotion pattern.**
   `StorefrontConfigChanged` gains an **appended, back-compatible `int DiscountBps = 0`**; Ordering's
   `StorefrontTaxCopy` gains `DiscountBasisPoints` and the projection consumer assigns it on **both** the
   insert and the update branch, so a re-projection is idempotent and a cleared discount actually clears.
   Migrations land in **both** services (`catalog."Storefronts"`, `ordering."StorefrontTaxCopies"`), each
   a nullable-free `AddColumn` with `defaultValue: 0` — existing rows mean "no discount" without a data
   backfill. Checkout reads the percentage off the **same `StorefrontTaxCopy` row it already loads by
   storefront id** for the ADR-0050 ship-to gate: no extra query, no cross-service call.

8. **Shown == charged.** The percentage is exposed on the public storefront config as
   `discountBasisPoints` (`discountBps` in the TypeScript client) and rendered as its own
   `Discount (10%)` row in the cart and the checkout summary, so the shopper sees the deduction named
   rather than a mysteriously smaller total. (ADR-0051 later added `GET /cart/summary`, which returns
   `storefrontDiscountMinor` as a distinct server-computed field; the cart page prefers it and falls back
   to the local percentage when the summary is unavailable, while the checkout summary still derives the
   storefront-wide part locally from `discountBps` over the summary's subtotal — the same formula, and the
   POST is authoritative either way.)

9. **No new ledger line and no new account.** The discount lowers the **charged gross**, and
   `Ledger.Sale` derives product revenue as `gross − tax − shipping`, so a discounted order posts less
   revenue automatically. Nothing is booked as a "discount expense" — a discount is forgone revenue, not
   a cost — so the trial balance stays 0 and ADR-0045 per-storefront attribution is untouched.

## Alternatives considered

- **Model it as a promotion (a storefront-scoped, thresholdless, always-eligible rule).** Superficially
  tidy, and rejected: it would drag the store setting into the combinability contest, where a single
  exclusive promotion could out-compete and *silence* the store's own sale — a store-wide sale that
  disappears when a campaign runs is not what "10% off everything" means. It would also have needed the
  promotion machinery (aggregate, projection, evaluator) months before that machinery existed. ADR-0051
  keeps the two deliberately separate for exactly this reason.
- **A decimal percentage column.** Rejected: basis points are exact, need no currency-decimal handling,
  and match the neighbouring `TaxRateBasisPoints`. The percent↔bps conversion lives in the admin UI only.
- **Discount the whole order (shipping and fees included).** Rejected: shipping is a pass-through cost
  with its own income account (ADR-0045) and its own taxability rules (ADR-0050); discounting it makes a
  store-wide sale silently subsidise carriers, and free shipping is a separate, explicitly-modelled reward
  (ADR-0051).
- **Tax on the *undiscounted* base.** Rejected outright — it overcharges tax in exclusive regimes and
  breaks the inclusive identity in AU/EU.
- **A ledger contra-revenue "discounts given" account.** Rejected for now: it would require posting a
  gross the customer never paid and then reversing it, for reporting the order snapshot already supports.

## Consequences

- **The money invariant is preserved, and is asserted per case.**
  `NetMinor − DiscountMinor + ShippingMinor + TaxMinor == GrossMinor` with `TrialBalanceAsync() == 0`
  (`MoneyFlowTests`: discounted items + full shipping + tax on the discounted base, a zero-discount
  control, and stacking on an effective offer). Note the field naming: `Order`/`CheckoutAttempt.NetMinor`
  is the **pre-discount items subtotal**, which is why the identity carries the `− DiscountMinor` term.
- **`StorefrontConfigChanged` is append-only.** `DiscountBps` is the last positional parameter with a
  default; an older publisher simply projects 0. Every future field on this contract must be appended the
  same way.
- **The order snapshots the combined discount, not the split.** `CheckoutAttempt.DiscountMinor` is the
  total; ADR-0051 added `PromotionDiscountMinor` beside it, so the storefront-wide share is derivable as
  `DiscountMinor − PromotionDiscountMinor`. There is deliberately no third column.
- **`PricingEngine` and `CheckoutEndpoints` each implement the discount step.** At the time this shipped
  they were two separate money paths (the dead-code split ADR-0051 later diagnosed), so the same two-line
  computation appears in both, with matching rounding, and `PricingTests` + `MoneyFlowTests` pin the two
  to the same answers. ADR-0051 unified the *promotion* decision behind one evaluator but left this step
  duplicated; it is small, uniform, and has no eligibility to get wrong.
- **The taxable share started proportional and is now exact.** As shipped, the taxable portion of the
  discount was `discount × taxableSubtotal / subtotal` — exact for a uniform store-wide percentage, and
  wrong once ADR-0051 introduced product-scoped promotions whose discount can land entirely on an exempt
  line. Checkout now allocates the storefront-wide part per line
  (`PromotionEvaluator.AllocateProportionally`, largest remainder) and sums only the allocations on
  destination-taxable lines. `HomeRegimeTaxStrategy` in `Pricing.cs` still uses the proportional ratio,
  which remains correct for its own (uniform) inputs.
- **Checkout resolved the discount by storefront id, while the tax rate was still resolved by currency**
  (`IsLive`, highest rate). Two different resolution rules read from the same table; the discount's was the
  precise one. Aligning the tax lookup was called out here as a separate, pre-existing concern — and it
  was: a 0% store charged a same-currency neighbour's rate *and* its inclusiveness. **Fixed by
  [ADR-0055](./0055-discounted-refund-basis-and-storefront-scoped-tax.md)**, which resolves tax from the
  same storefront copy the discount already used.
- **Rounding is `AwayFromZero`**, so a half-minor-unit tie rounds the discount *up* — in the shopper's
  favour, and consistently in the admin's percent→bps conversion, the engine, checkout, the cart preview
  and the TypeScript client.
- Out of scope, and still open: scheduled sale windows (a start/end on the setting rather than an operator
  toggling it), category- or collection-limited store sales, and a compare-at/"was" price on the product
  listing. The first two are better served by promotions (ADR-0051) than by growing this setting.
