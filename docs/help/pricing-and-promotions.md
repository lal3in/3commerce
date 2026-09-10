# Pricing & promotions — from supplier cost to the charged total

**The one page that explains the whole money chain.** Every other pricing document describes one link:
[ADR-0047](../adr/0047-storefront-scoped-active-window-offer-price.md) the offer price,
[ADR-0051](../adr/0051-threshold-promotions-and-combinability.md) threshold promotions,
[ADR-0052](../adr/0052-coupon-codes-and-redemption-limits.md) coupon codes,
[ADR-0038](../adr/0038-per-currency-shelf-prices-and-tax-entry.md) tax entry,
[ADR-0050](../adr/0050-per-country-ship-rules-and-ship-to-allowlist.md) per-country ship rules.
This page puts them in order, says exactly **which number each rule compares against**, and works one
cart all the way to the amount authorized on the card.

Everything here is integer **minor units** (cents) plus an ISO-4217 code. There is **no FX anywhere** —
a EUR promotion simply never applies to an AUD cart ([ADR-0041](../adr/0041-per-store-order-costs-chargebacks-and-estimated-margin.md)).

---

## 1. The chain at a glance

```
   supplier cost (COGS)          ← cost side. NEVER customer-facing.
        ┊ (Offer.SupplierCostMinor — posted later as a supplier payable)
        ┊
 ┌──────┴──────────────────────────────────────────────────────────────────────┐
 │ 1. catalog price          Variant per-currency shelf price (ADR-0038)       │
 │ 2. effective offer price  active + in-window + this storefront + this        │
 │                           currency + APPROVED supplier (ADR-0047/0048)       │
 │                           → wins over the catalog price, no drift-409        │
 │ 3. subtotal   = Σ (effective unit price × qty)   ◄── THE COMPARISON BASE     │
 │                 excl. tax, shipping and fees                                 │
 │ 4. promotions + coupons   thresholds measured on (3); rewards computed on    │
 │                           the promotion's OWN scope base; combinability      │
 │                           picks the winning set (ADR-0051/0052)              │
 │ 5. storefront-wide %      a store SETTING on (3) — never a promotion         │
 │ 6. discountMinor = min(promotionDiscount + storefrontDiscount, subtotal)     │
 │ 7. shipping               0 if free shipping won / nothing shippable /       │
 │                           collect-at-warehouse / every line ships covered    │
 │ 8. tax                    on the DISCOUNTED taxable base (+ taxable shipping)│
 │ 9. Net − Discount + Ship + Tax = Gross   (NetMinor is PRE-discount)          │
 │    and the ledger trial balance stays 0                                      │
 └──────────────────────────────────────────────────────────────────────────────┘
```

Two rules carry most of the confusion, so they are stated plainly:

- **Every threshold is measured on the offer-resolved item value — step 3 — never on the total the
  shopper pays.** Tax, shipping and fees are excluded, and the **storefront-wide discount does not
  reduce it** (that discount is applied *after* the promotion decision). "Spend $100 and ship free"
  means $100 of goods at their effective price.
- **Scope decides both what is measured and what is discounted.** A **storefront-scoped** promotion
  measures the whole cart and discounts **all** item lines. A **product-scoped** promotion measures only
  that product's lines (summed across its variants) and discounts **only that product's lines**.

---

## 2. Link by link

### 2.1 Supplier cost (COGS) — the cost side

`Offer.SupplierCostMinor` is what the platform pays the supplier. It is edited in exactly one place —
the Admin **Suppliers** console — and is shown read-only on the Catalog editor and the `/offers` form.
It **never** enters the customer-facing chain: it does not set a price, does not feed a threshold, and
does not appear on the storefront. After an order confirms it becomes a **supplier payable accrual**
(`OrderConfirmed` → Payments), which is how per-store contribution margin is computed
([ADR-0041](../adr/0041-per-store-order-costs-chargebacks-and-estimated-margin.md)). See
[Supplier functionality & management](./supplier-functionality.md).

### 2.2 Catalog price

Each `ProductVariant` carries **per-currency shelf prices** ([ADR-0038](../adr/0038-per-currency-shelf-prices-and-tax-entry.md)).
A product with no price in the storefront's currency is not sold there — add-to-cart is rejected rather
than mis-priced. Carts are single-currency; adding an item in another currency is a 409.

### 2.3 Effective offer price ([ADR-0047](../adr/0047-storefront-scoped-active-window-offer-price.md) / [ADR-0048](../adr/0048-supplier-approval-gated-offer-availability.md))

An `Offer` price becomes the **authoritative charge** for a line when **all** of these hold:

| Condition | Detail |
|---|---|
| Active | `Offer.Active` |
| In window | `ActiveFrom`/`ActiveUntil` (either may be open-ended) |
| Storefront match | `Offer.StorefrontId` is null (all storefronts of its currency) or equals this store |
| Currency match | the offer's currency equals the cart currency — never converted |
| **Approved supplier** | the offer's supplier is `Active` in Entity (Decision A, strict — ADR-0048) |

When one applies, `UnitPriceMinor` is the offer price, the storefront shows the same number
(*shown == charged*), and it does **not** trip the price-drift 409 — the shopper saw that price. A line
whose only covering offers come from unapproved suppliers is **rejected at checkout**, not silently
re-priced. A line with no offer at all keeps plain catalog behaviour.

`ResolvePricingOffer(...)` is the single resolver, and both `POST /checkout` and `GET /cart/summary`
call it with the same arguments — which is why the preview and the charge agree.

### 2.4 Threshold promotions ([ADR-0051](../adr/0051-threshold-promotions-and-combinability.md))

Catalog owns the `Promotion` aggregate; `PromotionChanged` projects it into Ordering's `PromotionCopy`,
so checkout never queries Catalog ([ADR-0008](../adr/0008-database-per-service-single-postgres.md)).

| Field | Meaning |
|---|---|
| `scope` | `1` Storefront (whole cart) or `2` Product (`productId` required) |
| `minimumAmountMinor` | money threshold on the **scope base**; `0` = none |
| `minimumQuantity` | unit-count threshold on the scope base; `0` = none |
| both set | **AND** — both must be met (`>=`, so landing exactly on the threshold qualifies) |
| `grantsFreeShipping` | zeroes the cart's shipping charge when this promotion wins |
| `percentOff` **xor** `discountAmountMinor` | percent of, or fixed amount off, **its own scope base** (a fixed amount is clamped to that base, so $20 off a $5 product takes $5) |
| `combinable` | `true` stacks with other combinable promotions; `false` = **Exclusive** |
| `activeFrom`/`activeUntil` | inclusive window, either bound open-ended |
| `currency` | thresholds and fixed amounts are denominated here and never converted |
| `storefrontId` | null = every storefront of that currency |

**Selection — the better of two branches.** Every eligible promotion becomes a candidate; the engine then
compares

```
[ best single Exclusive ]   vs   [ Σ all Combinable ]
benefit = discountMinor + (freeShippingApplied ? shippingMinor : 0)
```

and keeps the branch with the greater **customer benefit**. Ties go to the combinable set (the shopper
sees more applied promotions for the same money); inside a branch ties break on ascending promotion id.
An Exclusive promotion never combines with anything — including other exclusives.

`shippingMinor` in that formula is **the rate the cart would otherwise pay**, so a cart that already
ships free (collect-at-warehouse, nothing shippable, every line `shippingCovered`) scores no phantom
benefit, and **free shipping only ever zeroes an already-computed charge** — it can never resurrect a
rate a quote guard rejected.

### 2.5 Coupon codes ([ADR-0052](../adr/0052-coupon-codes-and-redemption-limits.md))

**A coupon is not a new kind of thing — it is a `Promotion` with a `Code`.** Everything above still
applies: window, thresholds, scope, reward, and the same `Combinable` flag decides stacking.

| Field | Meaning |
|---|---|
| `code` | set ⇒ **code-gated** (applies only when the shopper enters it). Stored trimmed **UPPERCASE**, 1–40 chars of `A-Z 0-9 - _`, matched case-insensitively. **Unique per tenant** via a **filtered** unique index `(TenantId, Code) WHERE Code IS NOT NULL` — an unfiltered one would permit only a single code-less promotion per tenant |
| null `code` | **automatic** — the ADR-0051 behaviour, unchanged |
| `maxRedemptions` | total across all shoppers; `null` = unlimited. **Single-use is simply `1`** |
| `maxRedemptionsPerCustomer` | per shopper; `null` = unlimited |

A **code-gated promotion may carry no threshold at all** — the code *is* the gate ("10% off with
WELCOME10"). Consequently, *clearing* the code of a thresholdless coupon is refused: it would silently
become a store-wide sale.

**Redemption lifecycle — reserved at checkout, not at confirmation.** The charged amount and the payment
authorization are both fixed at checkout, so the coupon has to be locked in there; counting at
confirmation would let two shoppers both be charged a "last one" price:

```
POST /checkout  ── coupon validates AND wins ──▶  Reserved   (before AuthorizePayment)
Reserved        ── CheckoutCompleted           ──▶  Confirmed  (spent for good)
Reserved        ── OrderCancelled              ──▶  Released   (allowance handed back)
                   (failed payment · explicit cancel · the saga's 30-minute expiry
                    all publish OrderCancelled — one path, so nothing leaks)
```

Only a coupon that **actually won** the selection in §2.4 is reserved: one out-competed by a better
promotion discounted nothing, so it must not burn an allowance. Confirm and release are status-guarded
`UPDATE`s, so a redelivered message changes nothing, and a unique index on `(PromotionId, OrderId)` makes
a retried checkout idempotent.

**How the caps are race-safe.**

- *Total cap* — **one conditional statement whose rows-affected is the verdict**:
  `UPDATE ordering."PromotionCopies" SET "RedeemedCount" = "RedeemedCount" + 1 WHERE "PromotionId" = … AND ("MaxRedemptions" IS NULL OR "RedeemedCount" < "MaxRedemptions")`.
  Postgres row-locks for the duration, so concurrent checkouts serialize and the loser's predicate is
  re-evaluated against the winner's increment. A read-then-write (`COUNT(*)`, then insert) does **not**
  hold — which is the whole reason a counter column exists.
- *Per-customer cap* — there is no single row to serialize on, so the count is preceded by a
  **transaction-scoped advisory lock** keyed by `(promotionId, customerKey)`. Same mutual exclusion,
  over a key instead of a row. Both run inside one explicit transaction with the redemption insert.
- `RedeemedCount` lives on `PromotionCopy` but is **Ordering-owned and never projected** — a re-published
  `PromotionChanged` must not reset a cap and hand a limited code out again.
- **`CustomerKey` = `u:{userId}`** when signed in, else **`e:{trimmed, lowercased email}`**, so a guest
  checkout still counts and signing out does not reset a per-customer limit. The prefixes keep the two
  namespaces from colliding.
- A **stale-hold sweep** (reservations older than 45 minutes with no checkout attempt and no order)
  reclaims holds stranded by a crash between two commits. It runs before **either** counter is read to
  refuse a coupon — the global cap and the **per-customer** limit — gated on that limit actually looking
  reached, so a live checkout and a shopper with allowance left never touch it. The per-customer sweep is
  scoped to that one shopper's holds. Without it a single crash locked one shopper out of a coupon
  permanently: no other path could ever release the hold, and a promotion with no `MaxRedemptions` swept
  nothing at all.
- A reward **worth nothing here is never spent**: a free-shipping code on a cart that pays no shipping
  wins its comparison at a benefit of 0, is still shown as applied, but does not consume an allowance
  (`PromotionOutcome.ValuedPromotionIds` is the winning set minus those).

**Refusal reasons are distinct, not one blanket "invalid coupon".** `CouponStatus` crosses HTTP as a
**number** (platform invariant) and each member maps to its own localized storefront message:

| # | Status | Shown to the shopper (en) |
|---|---|---|
| 0 | `None` | — (nothing entered) |
| 1 | `Applied` | `Coupon {code} applied: {name}` |
| 2 | `UnknownCode` | We don't recognise the code `{code}`. |
| 3 | `Inactive` | The code `{code}` is no longer active. |
| 4 | `NotStarted` | The code `{code}` isn't available yet. |
| 5 | `Expired` | The code `{code}` has expired. |
| 6 | `WrongStorefront` | The code `{code}` can't be used on this store. *(also the no-FX currency mismatch)* |
| 7 | `ThresholdNotMet` | Your cart doesn't meet the conditions for `{code}`. |
| 8 | `UsageLimitReached` | The code `{code}` has reached its usage limit. |
| 9 | `CustomerLimitReached` | You've already used the code `{code}`. |

Two ordering decisions make those reasons truthful: the code is looked up by `(tenant, code)` **without**
the storefront/active/window filters, so a real code aimed at another store says so instead of sending
the shopper hunting a typo that isn't there; and **usage limits are checked before the threshold**,
because telling someone to spend more on an exhausted code is a lie.

> **Known gap — a guest's per-customer limit is only enforced at checkout.** `GET /cart/summary` is
> rendered before the shopper has typed an email, so an anonymous cart has no `CustomerKey` to count
> against and the preview reports the coupon as **applied**. Checkout — which has the email — then
> refuses with `CustomerLimitReached`. For a signed-in shopper the preview is exact. Closing it for
> guests would mean re-pricing on every keystroke of the email field; **the charge is never wrong, only
> the warning is late.**

### 2.6 Storefront-wide discount (ADR-0053)

`Storefront.DiscountBasisPoints` (0–10000; 0 = none) is a **store setting, not a promotion**. Set on the
Admin **Commerce ops** page as a percentage, stored as basis points, projected into Ordering's
`StorefrontTaxCopy` on `StorefrontConfigChanged` (appended `DiscountBps`, back-compatible default 0),
and carried by `DuplicateFrom` when a storefront is cloned.

- It is deducted from the **items' subtotal only** — **never shipping, never tax**.
- It applies **after** the per-line offer/catalog price is settled, so it rides whatever the shopper was
  actually charged per line.
- It is **not governed by `Combinable`** — that flag is promotion-vs-promotion only. The storefront
  discount always applies, stacking **additively** with whatever the promotion engine decided.
- The two are **jointly capped at the subtotal**, so goods can never be discounted below zero.
- It reaches the storefront app on the public config as `discountBasisPoints` (`discountBps` in the TS
  client) and renders as its own `Discount (5%)` row, so *shown == charged*.

### 2.7 Shipping

Shipping is **final before promotions are evaluated**, and is 0 when any of these hold: nothing in the
cart is shippable (product-type policy), **collect at warehouse** ([ADR-0049](../adr/0049-warehouse-collection-and-supplier-recorded-delivery.md)),
or **every** line's resolved per-country ship rule sets `shippingCovered`
([ADR-0050](../adr/0050-per-country-ship-rules-and-ship-to-allowlist.md)). Only after those guards — and
after the selected-quote validation (service + expiry, not expired) — can a winning free-shipping
promotion zero the remaining rate.

**The cart preview resolves shipping the same way** ([ADR-0054](../adr/0054-cart-preview-shipping-basis-and-promotion-parity.md)),
because a free-shipping promotion is worth exactly the shipping amount and can therefore *win or lose on
it*:

| The preview sees | Shipping basis |
|---|---|
| No line requires shipping (all digital/service) | **0** — free shipping is worth nothing, the contest is unambiguous |
| Every line's ship rule covers shipping for the destination | **0**, same reasoning |
| A shipping address is known — saved default **or** one the shopper already entered (a guest counts) | the **real carrier rate**, quoted from the same `POST /api/fulfillment/shipping/quote` the checkout rate picker uses (cheapest rate, cached 5 minutes per cart + destination) |
| A shippable cart and no address anywhere | **unknown** — see §2.7.1 |

The quoted rate reaches Ordering as `GET /cart/summary?shippingMinor=&shipToCountry=`. It is a **display
input only**: checkout still charges the quote it validates itself, so a client that sends a nonsense rate
only mis-informs itself.

#### 2.7.1 When the rate is not known yet: `basis`

`/cart/summary` returns a `basis` saying how settled the promotion decision is. The evaluator probes the
selection at shipping `0` and `subtotal + 1`, which provably straddles every point where the winner could
change:

| `basis` | Means | Shopper sees |
|---|---|---|
| `Settled` (0) | the same promotions win at **every** shipping amount | the normal rows — the preview cannot contradict the charge |
| `Quoted` (1) | the winner depends on shipping, and the amount is known | the normal rows, decided on the amount that will be charged |
| `Provisional` (2) | the winner depends on shipping, and the amount is unknown | the **guaranteed floor**, plus *"Free shipping may apply"* |

A provisional preview never asserts free shipping and never shows a goods discount larger than the charge:
it reports the smaller of the two possible discounts, so the shopper can only be charged the same or
better. Picking a shipping option at checkout re-prices the page, so the last thing they see before paying
is the verdict checkout applies.

### 2.8 Tax ([ADR-0038](../adr/0038-per-currency-shelf-prices-and-tax-entry.md) / [ADR-0050](../adr/0050-per-country-ship-rules-and-ship-to-allowlist.md) / [ADR-0055](../adr/0055-discounted-refund-basis-and-storefront-scoped-tax.md))

**Whose tax?** The rate *and* the regime (inclusive vs exclusive) come from **the storefront being
checked out** — its own projected `StorefrontTaxCopy` row, the same one the ship-to allowlist reads.
They are never resolved by currency across storefronts: two live stores may share a currency and charge
completely different tax, which is exactly what a 0% store did *wrong* before ADR-0055 (it picked up a
same-currency neighbour's 25% inclusive regime). Two consequences an operator will notice:

- A storefront that is **not live** (Draft / Paused / Archived) **cannot be checked out** — the request
  is refused with a 400 before any payment intent exists, rather than quietly selling untaxed.
- A session with **no storefront context at all** gets no tax estimate on the checkout page (and cannot
  complete a checkout anyway — every order belongs to a real storefront).

Tax is computed **last, on the discounted base**:

```
taxableSubtotal  = Σ line totals EXCLUDING lines whose ship rule sets chargeDestinationTax = false
taxableDiscount  = Σ (promotion per-line allocation + storefront-wide per-line share)
                     over destination-taxable lines only,  clamped at taxableSubtotal
taxableShipping  = shipping, or 0 when taxableSubtotal is 0 (a fully exempt cart)
taxBase          = taxableSubtotal − taxableDiscount + taxableShipping

inclusive (AU GST / EU VAT):  tax = round(taxBase × bps / (10000 + bps))   net = chargeBase
exclusive (US sales tax):     tax = round(taxBase × bps / 10000)           net = chargeBase + tax
chargeBase       = subtotal − discountMinor + shippingMinor
```

The **exact per-line** figures matter: a single proportional ratio (`discount × taxableSubtotal / subtotal`)
is right for a uniform storefront-wide percentage but **wrong** for a product-scoped promotion whose
discount may land entirely on a tax-exempt line. The promotion part therefore comes from the evaluator's
per-line vector, and the storefront-wide part is spread by largest remainder — see §4.

### 2.9 The invariant

`NetMinor − DiscountMinor + ShippingMinor + TaxMinor = GrossMinor`, exactly as those fields appear on
`CheckoutResponse` and on the order. **`NetMinor` is the PRE-discount subtotal** (`Order.NetMinor =
subtotal`), so the discount term is not optional: the shorter `Net + Ship + Tax = Gross` — as written in
[ADR-0051](../adr/0051-threshold-promotions-and-combinability.md) step 10 and repeated in several places
since — is false against a real response the moment any discount applies, and
[ADR-0053](../adr/0053-storefront-wide-items-discount.md) records the precise form. Neither a promotion, a coupon nor the
storefront-wide discount creates a ledger line: the charged gross simply drops, so the sale posts less
revenue and the **trial balance stays 0** ([ADR-0045](../adr/0045-mandatory-per-storefront-ledger-attribution.md)).
Every promotion/coupon integration case asserts it.

---

## 3. Worked example

**Store:** US-style storefront, USD, tax **exclusive** at 8.5% (850 bps), storefront-wide discount **5%**
(500 bps). Ship-to US, no ship-rule exemptions.

**Cart**

| Line | Catalog price | Effective offer | Unit charged | Qty | Line total | Supplier cost (COGS) |
|---|---|---|---|---|---|---|
| A · Widget | $30.00 | **$25.00** (active, in-window, this store, approved supplier) | 2500 | 2 | **5000** | 1200 ea |
| B · Gadget | $40.00 | — (no offer) | 4000 | 1 | **4000** | 2100 |
| | | | | | **subtotal 9000** | COGS 4500 |

Selected carrier rate: **$4.99** (499).

**Promotions live on this store**

| | Name | Scope | Threshold | Reward | Combinable |
|---|---|---|---|---|---|
| P1 | Free shipping over $75 | Storefront | amount ≥ 7500 | free shipping | ✅ stacks |
| P2 | 10% off Widgets, buy 2 | **Product** (Widget) | qty ≥ 2 | 10% off | ✅ stacks |
| P3 | $8 off orders over $80 | Storefront | amount ≥ 8000 | $8.00 fixed | ❌ exclusive |

**Step 3 — the comparison base** is `9000` (offer-resolved, excl. tax/shipping; the 5% store discount has
**not** been applied yet).

**Step 4 — candidates**

| | Scope base | Threshold met? | Discount | Free ship |
|---|---|---|---|---|
| P1 | 9000 (whole cart) | 9000 ≥ 7500 ✅ | 0 | yes |
| P2 | **5000** (Widget lines only) | qty 2 ≥ 2 ✅ | 5000 × 10% = **500** | no |
| P3 | 9000 | 9000 ≥ 8000 ✅ | **800** | no |

**Selection** (`shippingMinor` = 499, the rate the cart would otherwise pay):

- best single **Exclusive** = P3 → benefit `800 + 0` = **800**
- Σ **Combinable** = P1 + P2 → benefit `500 + 499` = **999**

`999 ≥ 800` → **the combinable pair wins.** `AppliedPromotionIds = [P1, P2]`,
`promotionDiscount = 500`, `freeShippingApplied = true`.

**Per-line allocation** — P2 is product-scoped, so its 500 falls **entirely on line A**:
`LineDiscountsMinor = [500, 0]`, which sums to 500 exactly.

**Step 5 — storefront-wide 5%:** `round(9000 × 500 / 10000)` = **450**, spread by largest remainder over
line value → `[250, 200]` (sums to 450 exactly).

**Steps 6–9**

| Step | Value |
|---|---|
| `discountMinor` = min(500 + 450, 9000) | **950** |
| `shippingMinor` (free shipping won) | **0** |
| `taxableDiscount` = (500+250) + (0+200) | 950 |
| `taxableShipping` (shipping is 0) | 0 |
| `taxBase` = 9000 − 950 + 0 | 8050 |
| `tax` = round(8050 × 850 / 10000) | **684** |
| `Net` = 9000 − 950 | 8050 |
| **`Gross` = 8050 + 0 + 684** | **8734 → $87.34** |

Invariant: `8050 + 0 + 684 = 8734` ✅ · trial balance 0 ✅ · gross margin before fees =
`8050 − 4500 = 3550`.

**Same store, tax-inclusive instead (AU GST 10%).** Nothing above steps 6–7 changes.
`tax = round(8050 × 1000 / 11000) = 732`, `net = chargeBase = 8050` — the shopper pays **$80.50** and
**$7.32** of it is the contained GST.

### 3.1 Same cart, with coupon `SAVE25`

`SAVE25` = 25% off, storefront scope, **no threshold** (the code is the gate), **exclusive**,
`maxRedemptions = 200`, `maxRedemptionsPerCustomer = 1`.

| | Benefit |
|---|---|
| best Exclusive = **SAVE25** → 9000 × 25% = 2250 | **2250** |
| Σ Combinable = P1 + P2 | 999 |

`999 ≥ 2250` is false → **SAVE25 wins alone.** No free shipping this time, so the $4.99 rate stands, and
P1/P2/P3 apply nothing.

| Step | Value |
|---|---|
| coupon allocation over `[5000, 4000]` | `[1250, 1000]` |
| storefront-wide 5% = 450 → allocation | `[250, 200]` |
| `discountMinor` = min(2250 + 450, 9000) | **2700** |
| `shippingMinor` | 499 |
| `taxBase` = 9000 − 2700 + 499 | 6799 |
| `tax` = round(6799 × 850 / 10000) | **578** |
| `Net` = 9000 − 2700 | 6300 |
| **`Gross` = 6300 + 499 + 578** | **7377 → $73.77** |

Because SAVE25 is in `AppliedPromotionIds`, **one redemption is reserved** immediately before
`AuthorizePayment`, and `CheckoutAttempt.CouponCode = "SAVE25"`. Had the combinable pair out-scored it
(as in §3), the coupon would have discounted nothing and **no allowance would have been burned** —
`CouponCode` would be null on the attempt and the response.

### 3.2 The same cart, previewed

The preview runs the same evaluator on the same candidates — the only question is what it scores free
shipping against ([ADR-0054](../adr/0054-cart-preview-shipping-basis-and-promotion-parity.md)).

- **Address known** (saved, or entered at checkout): the real rate is quoted — 499 in §3 — so the preview
  IS §3, down to the promotion ids. `basis = Quoted`.
- **All-digital cart**: shipping is 0, P1's free shipping is worth nothing, and P3's $8 beats P2's $5.
  `basis = Settled`; no hedging, because no rate could change it.
- **Shippable cart, no address yet**: P1+P2 (`500 + S`) races P3 (`800`) — they cross at `S = 300`, so the
  winner genuinely depends on the rate. The preview shows the **floor**: the combinable pair's `500` off
  the goods, *without* claiming free shipping, plus "Free shipping may apply at checkout". Whatever rate
  the shopper's address later produces, they are charged `500` off (plus free shipping) or `800` off —
  never less than they were shown. `basis = Provisional`.

> Before ADR-0054 this last case guessed a flat **499**, which is on the other side of that `S = 300`
> crossing from a slow-mail or interstate rate — so the cart could show one reward and checkout charge
> under another. That gap is closed: the preview either knows the rate, proves the rate cannot matter, or
> says it is provisional.

---

## 4. Architecture note — one evaluator, exact allocation

Before [ADR-0051](../adr/0051-threshold-promotions-and-combinability.md), `PricingEngine`
(`src/Services/Ordering/Domain/Pricing.cs`) was **not on the production money path**:
`CheckoutEndpoints.Checkout` re-implemented the whole money computation inline. That split was a real
correctness hazard — `PricingTests` could stay green while checkout charged a different amount. ADR-0051
closed it.

**`PromotionEvaluator` (`src/Services/Ordering/Domain/PromotionEvaluator.cs`) is now the single place a
promotion is decided.** It is pure — no EF, no HTTP, no ambient clock (time is a parameter) — and it owns
eligibility, threshold measurement, reward computation, combinability selection and the per-line
allocation. Three callers share it:

| Caller | Uses |
|---|---|
| `PricingEngine.Price` | `CandidateFor` for `Threshold` promotions, then `Select`. Legacy engine-only kinds 1–8 (coupon/category/bundle/tier) keep their own eligibility vocabulary and hand over a ready-made candidate; they default to `Combinable = false`, so their selection reduces exactly to the historical "best single promotion wins" |
| `CheckoutEndpoints.Checkout` | `Evaluate` over the projected `PromotionCopy` rows — the charge |
| `GET /cart/summary` | `Preview` — the same candidate set, probed against the shipping amount rather than scored once, so the preview reports a `basis` and can say the reward is not decided yet ([ADR-0054](../adr/0054-cart-preview-shipping-basis-and-promotion-parity.md)) |

`CouponValidator` also delegates its "does this cart qualify?" question to `CandidateFor`, so *whether a
coupon applies* is decided by exactly the code that computes its discount.

**Largest-remainder allocation.** `PromotionOutcome.LineDiscountsMinor` is parallel to the input lines
and sums to `DiscountMinor` **exactly**. A naive per-line `amount × share / total` loses pennies and
breaks `Net + Ship + Tax = Gross`; the evaluator instead floor-divides (in `Int128`, so a large cart
cannot overflow, and with no floating point anywhere) and hands the leftovers to the largest fractional
remainders first, ties to the earliest line. Two follow-up passes keep the vector honest:

- when the subtotal cap shaves the total below the sum of the parts, the vector is re-scaled by the same
  rule so it still sums to the reported discount;
- `ClampToLineTotals` caps each line at its own total (stacked promotions can over-allocate one line) and
  pushes the excess onto lines with headroom — **the sum is preserved**, which is what the tax base and
  the money identity depend on.

`PromotionEvaluator.AllocateProportionally` exposes the same rule so checkout can apportion the
**storefront-wide** discount per line for the tax base, instead of a rounded ratio.

The outcome is snapshotted so a past charge can be explained without re-running anything:
`CheckoutAttempt.PromotionDiscountMinor` / `AppliedPromotionIds` (comma-joined) / `FreeShippingApplied` /
`CouponCode`, and `CheckoutAttemptLine.DiscountMinor` per line.

---

## 5. Where it is operated

### Admin

| Screen | What you set |
|---|---|
| **Promotions** (`/promotions`) | Name, currency, scope (whole cart / one product), thresholds, rewards, `Combinable`, storefront, active window, **Code**, **Max redemptions**, **Max per customer**; the table shows **Code** (or "Automatic") and **Redemptions** used-vs-cap. See [Admin operations](./admin-operations.md) |
| **Commerce ops** (`/commerce-ops`) | The storefront-wide **Discount %** (create form + per-storefront Manage form; a **Discount** column in the table) |
| **Offers & pricing** (`/offers`) | The storefront-scoped, active-window **offer price** (ADR-0047) |
| **Suppliers** (`/suppliers`) | The per-variant **supplier cost** (COGS) — the only place it is edited |
| **Currencies** (`/currencies`) | Which currencies exist, and their decimals ([Currencies](./currencies.md)) |

### Storefront

The cart (`/cart`) and checkout (`/checkout`) both render `GET /cart/summary`: `Subtotal`,
`Discount (n%)`, one `Promotion: {name}` row per applied promotion, `Free shipping`, and `Items total`.
The coupon box lives on checkout — a plain GET form to `/checkout?coupon=…` so the **server** prices it,
the URL is refresh-safe and shareable, and **Remove** is just a link back to `/checkout`. Only a status
of `Applied` rides the checkout POST as a hidden field; a refused code renders its own reason and is left
out, so the charge always equals the total on screen. See [Storefront operations](./storefront-operations.md).

### API

| Method | Path | Purpose |
|---|---|---|
| `GET`/`POST` | `/api/catalog/admin/promotions` | List / create a promotion or coupon |
| `PUT` | `/api/catalog/admin/promotions/{id}` | Partial update (`applyScope` / `applyCode` / `applyUsageLimits` opt-ins) |
| `POST`/`PUT` | `/api/catalog/admin/storefronts[/{id}]` | `discountBasisPoints` (0–10000) |
| `GET` | `/api/catalog/storefronts/public` | Public config incl. `discountBasisPoints` |
| `GET` | `/api/ordering/cart/summary?storefrontId=&couponCode=&shippingMinor=&shipToCountry=` | The money preview + `couponStatus` + `basis` (ADR-0054) |
| `POST` | `/api/fulfillment/shipping/quote` | Carrier rates; the preview and the checkout rate picker call it with the same origin/parcel |
| `POST` | `/api/ordering/checkout` | Charges; accepts `couponCode`, reserves the redemption |
| `GET` | `/api/ordering/admin/promotion-redemptions` | Per-promotion usage (held / confirmed / cap) |

Full request/response detail: [API contracts index](../api/api_contracts_index.md).

---

## 6. Tests that hold this together

| Suite | Guards |
|---|---|
| `Catalog/tests/PromotionTests.cs` | Aggregate invariants: ≥1 threshold (automatic only), ≥1 reward, percent XOR fixed, scope↔product binding, ordered window; coupon normalization, character/length rules, thresholdless coupon allowed, clearing that code refused, usage-limit bounds |
| `Catalog/tests/StorefrontDiscountTests.cs` | `SetDiscount` bounds, duplication carries it |
| `Ordering/tests/PromotionEvaluatorTests.cs` | Threshold AND, scope bases, fixed-amount clamp, best-of selection, tie → combinable, ascending-id tiebreak, allocation sums exactly |
| `Ordering/tests/PromotionPreviewTests.cs` | The preview contract (ADR-0054): the flat-fallback divergence reproduced, a known rate matching the charge exactly, an all-digital cart scoring free shipping at 0, `Settled` vs `Provisional`, the guaranteed floor, and a 400-trial sweep proving the preview never contradicts the charge |
| `Ordering/tests/PricingTests.cs` | Engine parity, storefront-discount stacking + cap, tax on the discounted base (both regimes) |
| `Ordering/tests/CouponTests.cs` | The code gate, one fact per `CouponStatus`, customer-key rule |
| `IntegrationTests/MoneyFlowTests.cs` | End-to-end money with trial balance 0 for every discount shape |
| `IntegrationTests/PromotionMatrixTests.cs` | The discount × promotion × shipping matrix, preview **and** charge on the same cart: the free-shipping/cash-discount race at a real rate, the provisional path, an all-digital cart, exclusives never summing, exclusive vs stack (both directions), a tie, an unmet threshold, free shipping + store-wide, free shipping + product-scoped, coupon + free shipping + store-wide, a coupon losing without burning its allowance, and a stack capped at the subtotal — each asserting `Net − Discount + Ship + Tax = Gross` and trial balance 0 |
| `IntegrationTests/PromotionProjectionTests.cs` | `PromotionChanged` → `PromotionCopy` insert / idempotent re-consume / deactivate |
| `IntegrationTests/CouponAllowanceTests.cs` | What an allowance may be spent on, and what checkout WRITES DOWN: a hold stranded by a crash no longer locks that shopper out forever (and an in-flight hold still counts), a reward worth 0 does not burn a single-use code and the next shopper still gets it, and the PERSISTED per-line discount is read back out of the database — a product-scoped promotion lands wholly on its own line and the vector sums to `PromotionDiscountMinor` |
| `IntegrationTests/CouponRedemptionTests.cs` | **Ten concurrent checkouts vs `MaxRedemptions = 3`** (exactly 3 win), guest per-customer limit by email, failed payment releases the hold, redelivered messages neither double-confirm nor double-release, stale-hold sweep |
| `Ordering/tests/OrderLineDiscountsTests.cs` | The per-line refund basis (ADR-0055): a uniform storefront percentage spread by line value, a product-scoped promotion staying on its line, the two stacked, rounding remainders summing exactly, and the subtotal clamp |
| `IntegrationTests/StorefrontTaxScopingTests.cs` | Two live storefronts in **one currency** at 0% exclusive and 25% inclusive each charging their own rate and regime, a low-rate store next to a louder one, another tenant's store in the same currency, and the non-live refusal |
| `IntegrationTests/RmaRefundBasisTests.cs` | A return refunds the **discounted** line value (2800, not the 4000 it was listed at), a full return of a discounted order is capped at the refundable gross and reaches `RefundIssued` instead of stranding, per-unit pro-rating on a partial return, the pre-ADR-0055 back-compat path, the second-request cap, and `RefundFailed` as a terminal state |
| `e2e/promotion-shipping-parity.spec.ts` | The provisional wording on a cart with no address, and the cart settling on the real carrier rate once one is entered |
| `e2e/storefront-{discount,promotions,coupons}.spec.ts`, `e2e-admin/{commerce-ops-discount,promotions-admin}.spec.ts` | The browser flows: authoring in admin, the shopper seeing the rows, apply/remove |

`scripts/e2e-verify.sh` runs the focused unit set as stage **A3b**; see [Testing](./testing.md).
