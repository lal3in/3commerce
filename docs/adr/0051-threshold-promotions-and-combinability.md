# 0051 — Threshold-based promotions (money and/or quantity) and promotion combinability

Status: Accepted
Area: Catalog / Ordering / pricing
Extends: [0047](./0047-storefront-scoped-active-window-offer-price.md) (the offer-resolved effective selling price — the comparison base), [0048](./0048-supplier-approval-gated-offer-availability.md) (an unapproved supplier's offer never sets a price, so it never feeds a threshold), [0038](./0038-per-currency-shelf-prices-and-tax-entry.md) (inclusive vs exclusive tax on the discounted base), [0050](./0050-per-country-ship-rules-and-ship-to-allowlist.md) (`chargeDestinationTax` / `shippingCovered`), [0008](./0008-database-per-service-single-postgres.md) (read-copy projections, no cross-service query), [0045](./0045-mandatory-per-storefront-ledger-attribution.md) (no new ledger line — the charged gross simply drops)

## Context

The platform had **no promotions**. What it did have was a *dormant* promotion engine in
`src/Services/Ordering/Domain/Pricing.cs` (lines 10-279): a rich `PromotionKind` enum, a `Promotion`
record with eligibility, best-rule selection and free-shipping zeroing — and nothing feeding it. There
was no `Promotion` entity, no table, no admin surface, no bus contract, and `CheckoutEndpoints` never
constructed a `Promotion` nor mentioned `PricingEngine`. Only the unit tests exercised it.

Three facts shaped the decision:

1. **The existing engine could not express the requirement.** `Promotion` carried `MinimumQuantity`
   but no *money* threshold, and no combinability flag: `PricingEngine.Price` hard-coded "exactly one
   promotion wins" (`.OrderByDescending(...).FirstOrDefault()`), and `PricingResult` could report only a
   single `AppliedPromotionId`.

2. **`PricingEngine` is not on the production money path.** `CheckoutEndpoints.Checkout`
   re-implements the whole money computation inline — subtotal, ship-rule tax exemption, the shipping
   waiver, the storefront-wide discount (PR #256), proportional taxable-discount allocation, and
   inclusive-vs-exclusive tax. Extending only `Pricing.cs` would leave `PricingTests` green **while
   checkout charged the wrong amount**. This dead-code split is the single largest correctness risk in
   the feature, and this ADR resolves it.

3. **Checkout must not query Catalog** (ADR-0008). Any Catalog-owned promotion has to reach Ordering as
   a projected read copy, or checkout cannot see it.

There is also a neighbouring, deliberately *separate* concept: the **storefront-wide items discount**
(PR #256) — a blanket percentage on the `Storefront` aggregate. It is a store setting, not a promotion,
and the combinability flag introduced here does not govern it.

## Decision

1. **Catalog owns the `Promotion` aggregate.** A new `Promotion` entity + `/admin/promotions`
   (list / create / update) endpoints sit next to `Storefront` and `Offer` — the two things a promotion
   references. Catalog already publishes `StorefrontConfigChanged` and `OfferChanged` into Ordering, so
   the projection lane exists. (The dormant `Pricing` service was rejected: it is a Phase-7 `Price`
   aggregate with no bus wiring and no path to checkout; routing through it would need a brand-new
   service-to-service lane for zero gain.)

2. **`PromotionChanged` projects it into Ordering's `PromotionCopy`**, mirroring
   `OfferChanged`/`OfferCopy` exactly (append-only contract, back-compatible defaults, idempotent upsert
   consumer). Migrations land in **both** services.

3. **A promotion is a threshold rule.** It carries a **money threshold** (`MinimumAmountMinor`) and/or a
   **quantity threshold** (`MinimumQuantity`). At least one must be set. **When both are set they are
   ANDed — both must be met.** AND is the conservative reading of "amount and/or quantity": a promotion
   that fires when *either* threshold is met is strictly more generous and cannot be tightened later
   without changing behaviour for live campaigns, whereas AND can be relaxed. An operator who wants OR
   creates two promotions and marks them combinable.

4. **Scope is `Storefront` or `Product`.**
   - `Storefront` scope measures the **whole cart's** item value (or total unit count) and, when it wins,
     discounts **all** item lines.
   - `Product` scope measures **that product's** total item value (or that product's quantity, summed
     across every line/variant of it) and discounts **only that product's lines**. A product-scoped
     promotion whose product is absent from the cart is ineligible.

5. **The comparison base is the offer-resolved effective selling price × quantity, excluding taxes,
   shipping and fees** (ADR-0047): the effective `Offer` price when one applies for this
   storefront/currency/window, otherwise the catalog price. Supplier cost (COGS) is irrelevant — it is
   the cost side, never customer-facing. The **same base** is what a won discount applies to. An offer
   from an unapproved supplier never sets a price (ADR-0048), so it never contributes to a threshold.

6. **Rewards are free shipping and/or a discount**, where the discount is either a percentage (0–100)
   **or** a fixed minor-unit amount — never both. At least one reward must be set. A fixed amount is
   clamped to its own scope base.

7. **Currency is part of identity; there is no FX.** `MinimumAmountMinor` and the fixed
   `DiscountAmountMinor` are **denominated in the promotion's `Currency`** and are never converted
   (ADR-0041, strict no-FX posture). A EUR promotion simply never applies to an AUD cart.

8. **Combinability.** Every promotion carries a flag: `Exclusive` (only it applies) or `Combinable`
   (stacks with other combinable promotions). The engine evaluates **all** eligible promotions and takes
   the better of `[best single exclusive]` vs `[sum of all combinable]`, where "better" = greatest
   **customer benefit** = `discountMinor + (freeShippingApplied ? shippingMinor : 0)`. An exclusive
   promotion never combines with anything, including other exclusives. **Deterministic tie-breaks:** when
   the two branches score equally, the **combinable set wins** (it shows the shopper more applied
   promotions for the same money); within a branch, ties break on ascending `PromotionId`, mirroring the
   existing `.ThenBy(p => p.PromotionId)` in `Pricing.cs`.

9. **One shared `PromotionEvaluator`** (`src/Services/Ordering/Domain/PromotionEvaluator.cs`) — pure, no
   EF, no HTTP, time as a parameter — owns eligibility, threshold measurement, reward computation,
   combinability selection and the **per-line discount allocation**. Both `PricingEngine.Price` and
   `CheckoutEndpoints.Checkout` call it, so the promotion algorithm exists in exactly one place.
   `PricingEngine` keeps its own tax/shipping seam and checkout keeps its own (richer) one — only the
   promotion decision is shared. The evaluator returns `LineDiscountsMinor`, parallel to the input lines,
   allocated by **largest remainder** so `Σ LineDiscountsMinor == DiscountMinor` exactly.

10. **The normative pricing order** (implemented exactly as written):

```
1.  per line: UnitPriceMinor = effective offer price (ResolvePricingOffer, approval-gated)
                             ?? current catalog price
2.  subtotal        = Σ UnitPriceMinor × Quantity                        [the COMPARISON BASE — excl. tax/ship/fees]
3.  promotions      = PromotionEvaluator.Evaluate(lines, subtotal, shippingMinor, copies, now)
       3a. eligibility: tenant + storefront + currency + active + window + threshold met
       3b. per-promotion reward: discount on its own scope base, and/or free shipping
       3c. selection : better of [best single Exclusive] vs [Σ all Combinable]
                       benefit = discountMinor + (freeShipping ? shippingMinor : 0)
       3d. allocation: spread the won discount across the contributing lines (largest remainder)
4.  storefrontDiscount = round(subtotal × DiscountBasisPoints / 10000)   [PR #256 — always applied]
5.  discountMinor   = min(promotionDiscount + storefrontDiscount, subtotal)   [cap: goods never go negative]
6.  shippingMinor   = 0 if (freeShippingWon || !anyShippable || collectAtWarehouse || allShippingCovered)
                      else the validated selected/flat rate
7.  taxableDiscount = Σ line discount over destination-taxable lines only (exact, from step 3d + step 4's
                      proportional share)
8.  taxBase         = taxableSubtotal − taxableDiscount + taxableShipping
    tax             = inclusive ? round(taxBase × bps / (10000+bps)) : round(taxBase × bps / 10000)
9.  chargeBase      = subtotal − discountMinor + shippingMinor
    net             = inclusive ? chargeBase : chargeBase + tax
10. INVARIANT: Net + Ship + Tax = Gross  AND  trial balance = 0 (no new ledger line)
```

   **Why this order.** The promotion must be measured and applied on the *offer-resolved* base (step
   1→2), or a storefront running an offer would compute thresholds against a price the shopper never
   sees. Promotions are merchandising rules attached to specific carts; the storefront-wide discount is a
   blanket store setting, so it applies **after** and stacks additively — the identical shape PR #256
   already uses. Both are capped together at the subtotal so goods can never go negative. Tax is last, on
   the discounted base, so both ADR-0038 regimes stay correct. **Free shipping only ever zeroes an
   already-computed shipping charge**; it is applied *after* the collect-at-warehouse / non-shippable /
   `allShippingCovered` waivers and the quote guards, so it can never resurrect a rate the quote guards
   rejected, and it scores no phantom benefit on a cart that already ships free.

11. **`GET /cart/summary` (Ordering, anonymous, cookie-keyed) gives shown == charged.** It resolves offer
    prices + the storefront discount + promotions with the **same evaluator** and returns the money
    preview the storefront cart and checkout summary render. `GET /cart` still returns the add-time
    catalog price and is unchanged. The promotion algorithm is never re-implemented in TypeScript.

## Alternatives considered

- **Duplicate the promotion algorithm inside `CheckoutEndpoints`.** Faster to type and needs no refactor,
  but it institutionalises the divergence: `PricingTests` would go green while checkout charged something
  else — exactly the failure mode this codebase already had. Rejected.
- **Full unification: delete checkout's inline math and have it build a `PricingInput`.** The right
  long-term shape, but materially bigger and riskier — checkout also owns ship-rule exemptions,
  collect-at-warehouse, approval gating, quote validation, per-currency carts and the price-drift 409,
  none of which `PricingInput` models. Deliberately out of scope; its own ADR + PR.
- **A dedicated Promotions service.** Rejected for now (see Decision 1). `PromotionChanged` is the seam
  that makes the move cheap if it is ever wanted.
- **OR-ing the two thresholds.** Rejected — see Decision 3.

## Consequences

- **No new ledger line and no new account.** Exactly as PR #256 established: the promotion lowers the
  charged gross, so the sale posts less revenue and the trial balance stays 0. The per-storefront
  attribution invariant (ADR-0045) is untouched. Every new `MoneyFlowTests` case asserts
  `TrialBalanceAsync() == 0`.
- **The storefront-wide discount of PR #256 remains a separate, always-applied setting.** The
  combinability flag governs promotion-vs-promotion only; the two stack additively and are jointly capped
  at the subtotal.
- **`PricingResult` grows `AppliedPromotionIds`** (a collection), with `AppliedPromotionId` kept as a
  computed first-or-null so existing callers and tests still compile. **`CheckoutResponse` gains appended
  optional fields** (`FreeShippingApplied`, `AppliedPromotionIds`) — it is a positional record, so new
  fields must always be appended with defaults.
- **`CheckoutAttempt` snapshots the outcome** (`PromotionDiscountMinor`, `AppliedPromotionIds`,
  `FreeShippingApplied`) and `CheckoutAttemptLine.DiscountMinor` is now populated per line instead of
  hard-coded 0 — so a charge can be explained after the fact.
- **Per-line allocation replaces the proportional tax ratio.** The old
  `discountMinor * taxableSubtotal / subtotal` is exact for a uniform storefront-wide percentage but
  wrong for a product-scoped promotion (whose discount may fall entirely on a tax-exempt line). Checkout
  now sums only the allocations landing on destination-taxable lines (ADR-0050).
- Checkout loads `PromotionCopies` once, on an index of `(TenantId, StorefrontId, Active)`, alongside the
  copies it already loads — no extra cross-service call.
- Out of scope, tracked as follow-ups: coupon-code gating, per-shopper/global redemption caps, "you're $X
  away from free shipping" nudges, category-scoped thresholds, and buy-X-get-Y rewards.
