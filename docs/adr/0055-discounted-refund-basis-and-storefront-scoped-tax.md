# 0055 — A refund is worth what the line was SOLD for; tax belongs to a storefront, not to a currency

Status: Accepted — implemented
Area: Support / Payments / Ordering / pricing
Extends: [0038](./0038-per-currency-shelf-prices-and-tax-entry.md) (inclusive vs exclusive tax entry — *which storefront's* regime), [0018](./0018-support-tickets-rma-state-machine.md) (the RMA lifecycle this adds a terminal failure state to), [0014](./0014-stripe-only-v1-double-entry-ledger.md) (the single refund execution path), [0008](./0008-database-per-service-single-postgres.md) (the projections both fixes ride on), [0053](./0053-storefront-wide-items-discount.md) / [0051](./0051-threshold-promotions-and-combinability.md) / [0052](./0052-coupon-codes-and-redemption-limits.md) (the discounts that made both defects routine)

## Context

Two money-moving defects, both older than the promotion work and both harmless while discounts were
theoretical. Once storefront-wide discounts, threshold promotions and coupons landed, discounted orders
became the normal case and each defect started moving real money on every return.

### 1. Refunds were computed on the undiscounted list price

`OrderSnapshotLine` (Support's read copy of a confirmed order) carried `UnitPriceMinor` and `Quantity`
and nothing else, and both RMA paths computed `amount += UnitPriceMinor × qty`. The discount was not
merely unused — it was **invisible**: `OrderLineInfo` on `OrderConfirmed` did not carry it, so Support
could not have known the line's real value even in principle.

`OrderSnapshot.GrossMinor` — what the shopper actually paid — was projected and displayed, but never
used as a ceiling. Two failures followed, with an order of subtotal 9000, discount 2700, shipping 499,
tax 578, gross 7377:

* **Over-refund.** Returning one line listed at 4000 refunded 4000 for goods sold for ~2800. Payments'
  remaining-balance check passed it (4000 ≤ 7377), so the ~1200 loss was silent. Reproduced.
* **Silent stranding.** A full return computed 9000 > the 7377 remaining. `ExecuteRefundConsumer` logged
  a warning and **returned** — no `RefundCompleted`, no failure event — leaving the RMA saga in
  `RefundPending` forever: no refund, no error, nothing an operator could see. Reproduced (amount 9000,
  terminal state `RefundPending`).

### 2. Tax and inclusiveness were resolved from an arbitrary storefront

Checkout resolved the tax row with:

```csharp
.Where(t => t.IsLive && t.Currency == currency)
.OrderByDescending(t => t.TaxRateBasisPoints)
.FirstOrDefaultAsync()
```

— filtering on neither `StorefrontId` nor `TenantId`, though `StorefrontTaxCopy` carries both and no
global query filter applies. Every live storefront in a currency was one pool and the **highest rate
always won**. It also decided `TaxInclusive`, which changes whether tax is *added to* or *extracted
from* the charge, so a store could inherit a neighbour's entire tax regime. Reproduced: a live 0% store
checking out a 10000 cart was charged tax 2100 in the other store's inclusive regime.

The defect had also shaped the test suite — the promotion and coupon suites each invent a unique fake
three-letter currency (`QJJ`, `QKK`, `QK1`, …) purely so no other test's live storefront can be picked
up by their carts.

## Decision

### A. The refund basis is the line's discounted value, capped at the remaining gross

1. **`OrderLineInfo` carries `DiscountMinor`** — the line's whole allocated share of the order's
   discount — appended with a default `0`, so an `OrderConfirmed` published before this change (or a
   consumer that has not been redeployed) reads "no discount known" rather than breaking.

2. **The allocation combines two things checkout stores separately.** `OrderLine.DiscountMinor` holds
   only the *promotion* allocation (product-scoped promotions land on the lines they cover, ADR-0051);
   the storefront-wide percentage stays at the order level (ADR-0053). `OrderLineDiscounts` (Ordering
   domain, pure) puts them together: the promotion vector keeps the placement checkout gave it, and the
   remainder is spread by line value with the **same largest-remainder rule**, so the parts sum to
   `Order.DiscountMinor` exactly. Where checkout's subtotal clamp cut the promotion back, the order's
   *actual* discount is spread over the promotion vector's own shape rather than the raw vector.

3. **`OrderSnapshotLine` mirrors the field** (migration `AddColumn` default 0) and answers
   `RefundableMinor(qty)` = `UnitPriceMinor × qty − (the same share of the line's discount)`, pro-rated
   over a partial return and never below zero. A full return of a line always gives back exactly what
   the line was sold for.

4. **`OrderSnapshot.GrossMinor` is the ceiling.** Line values can only ever *estimate* what the shopper
   paid — the snapshot knows nothing of shipping, tax or an earlier partial refund — so both RMA paths
   finally cap the request at `GrossMinor − Σ (earlier non-denied requests)`. Nothing left ⇒ a 400, not
   a zero-value RMA. **This is what keeps pre-existing snapshots safe** (see Consequences).

5. **Every refusal is announced.** `RefundFailed(RefundId, OrderId, AmountMinor, Reason, Detail)` is
   published on all three non-success exits from `ExecuteRefundConsumer` — no captured payment, amount
   over the remaining balance, provider decline. The RMA saga correlates it by `RefundId` to a terminal
   `RefundFailed` state carrying the reason, published as `RmaStateChanged` (so the shopper is emailed)
   and rendered on the admin RMA queue row. *Rejected alternative:* keep the silent return and rely on
   the cap. A cap the requester cannot see is a cap that fails silently the next time an assumption
   about the snapshot is wrong; the whole point of the defect is that nothing said anything.

6. **Shown == refunded.** `RefundableLineDto` and `CustomerRmaLineDto` gained appended
   `RefundableAmountMinor` / `RefundAmountMinor` — the discounted value — and the storefront renders it
   instead of letting the shopper multiply a shelf price the refund will not honour.

### B. Tax is resolved from the storefront being checked out

7. **Reuse `storefrontCopy`.** The ship-to allowlist gate a few lines earlier already loads this
   storefront's `StorefrontTaxCopy` as an entity; rate and inclusiveness now come from it. No second
   query, and the scope is structurally impossible to omit.

8. **NOT LIVE = NOT SELLING.** The old query filtered `IsLive`, so a Draft/Paused/Archived storefront
   never contributed a rate. Now that the rate comes from *this* store's copy, "not live" can no longer
   silently mean "sell it untaxed" — checkout **refuses with a 400** before any payment intent exists.
   *Rejected alternative:* charge zero tax on a non-live store. That under-collects a tax liability on a
   sale nobody meant to make, which is the worse of the two failures; and Catalog only exposes a public
   config for `Active`/`Preview` stores, so a shopper cannot legitimately reach a paused one anyway. A
   storefront with **no projected copy at all** keeps the historical no-tax fallback: that is the
   tenant-default context, not a store someone switched off.

9. **The rate follows the storefront's jurisdiction, not the cart's currency.** A storefront has one
   currency (ADR-0038) and carts are single-currency, so the two agree in practice; where they would
   not, the store's own regime is the right answer and a currency match is not.

10. **The storefront app stops guessing too.** `app/checkout/page.tsx` fell back to
    `getStorefrontConfig({ currency })` — the same unscoped lookup, in TypeScript. It showed a rate for
    an order that cannot even be placed (the checkout POST carries the resolved storefront id, and
    Ordering refuses a checkout without one), so it is gone: no storefront context now means no tax
    estimate.

11. **`GET /cart/summary` needed no change.** Its storefront-wide-discount lookup is already scoped by
    `StorefrontId`, and it computes no tax.

## Consequences

* **Pre-existing snapshots degrade safely, and this is deliberate.** An order confirmed before this
  change has `DiscountMinor = 0` on every snapshot line (column default), so its refund basis is the
  list price — exactly the old behaviour. For an order that *was* discounted, that over-values the
  goods; decision 4's gross cap is what bounds the damage: such an order can never refund more than it
  was charged, so the previous over-refund becomes at worst a full refund, and the previous *stranding*
  becomes a completed refund. Back-filling the historical snapshots would require re-deriving each
  order's allocation from Ordering, across a service boundary, for orders that mostly have no discount;
  the cap buys the safety without the migration. New orders are exact from the first `OrderConfirmed`
  published after deploy.
* **Deploy order does not matter.** The contract field is appended with a default, so an old Ordering
  publishing to a new Support projects 0 (capped), and a new Ordering publishing to an old Support is
  simply ignored by a consumer that does not read the field.
* **`RefundFailed` is a new terminal RMA state**, so the admin queue, Mission Control counts and the
  shopper's status badge all gained a state. It is retained (no `Finalize`), like `Denied`.
* **The fake-currency test scaffolding is no longer load-bearing for tax.** It can be simplified: with
  the lookup scoped, two live storefronts may now share a currency, which
  `StorefrontTaxScopingTests` proves directly. The scaffolding still isolates *promotions*, which are
  matched by `(tenant, storefront, currency)` — so the fake currencies remain useful there and were
  left alone rather than churned in a bug-fix change.
* Three test setups (`MoneyFlowTests` × 3, `CartCurrencyTests` × 2) seeded a live tax copy and relied
  on the by-currency lookup to find it; they now put the shopper on the storefront they seeded, which
  is what a real shopper does.

## Verification

* `RmaRefundBasisTests` — the over-refund (a 4000 line in a 2700-discounted order refunds 2800), the
  stranding (a full return of a discounted order reaches `RefundIssued`, capped at gross), per-unit
  pro-rating on a partial return, the pre-discount back-compat path, the second-request cap, and
  `RefundFailed` reaching a terminal state. Trial balance 0 throughout.
* `StorefrontTaxScopingTests` — two live storefronts in ONE currency at 0% exclusive and 25% inclusive,
  each charging its own rate and regime; a low-rate store next to a louder one; another tenant's store
  in the same currency; and the non-live refusal. Money identity `Net − Discount + Ship + Tax = Gross`
  (Net is the PRE-discount subtotal) and trial balance 0 on every settled path.
* `OrderLineDiscountsTests` — the allocation shapes: uniform storefront percentage, product-scoped
  promotion, the two stacked, rounding remainders, and the subtotal clamp.
