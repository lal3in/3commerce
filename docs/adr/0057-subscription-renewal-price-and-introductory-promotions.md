# 0057 — A promotion says whether it is an introductory offer or a permanent price

Status: Accepted — implemented
Area: Catalog / Ordering / Payments / pricing
Extends: [0051](./0051-threshold-promotions-and-combinability.md) (the promotion whose reward this qualifies), [0053](./0053-storefront-wide-items-discount.md) (the store-wide discount that deliberately does NOT carry), [0052](./0052-coupon-codes-and-redemption-limits.md) (a coupon is a code-gated promotion and carries the same flag), [0056](./0056-a-refunded-order-keeps-its-redemption.md) (the other lifecycle question ADR-0052 left unwritten)

## Context

A recurring line is sold once and charged forever. When the sale was discounted, nothing said what the
*renewals* cost — and the gap was invisible because the code had, accidentally, picked a side.

`OrderStatusConsumer` published `SubscriptionRequested` with `line.UnitPriceMinor` — the **undiscounted**
price — and `SubscriptionService.ChargeRenewalAsync` charges that stored `Subscription.PriceMinor` for
the life of the subscription. So every discount was already introductory: first period discounted,
renewals at list.

That is a perfectly reasonable policy. It just wasn't a decision, wasn't documented, wasn't tested, and —
most importantly — **could not be changed**, because there was nowhere to say otherwise. A tenant running
"20% off for as long as you stay" had no way to express it, and a tenant running "first month half price"
had no way to know they were already getting it.

It is the same failure shape as ADR-0055's refund basis: the discount simply was not carried to the place
that needed it, and the resulting number looked plausible enough that nobody questioned it.

## Decision

**The promotion says. `Promotion.AppliesToRenewals` (default `false`) decides whether its discount rides
a subscription's renewals or only the first period.**

* `false` — **introductory**. The first period is discounted; renewals charge the list price. The default,
  and byte-for-byte the behaviour before this ADR, so every existing promotion keeps doing exactly what it
  did.
* `true` — **permanent**. The discount rides every renewal for the life of the subscription.

The flag lives on the promotion because that is where the offer's *terms* live — "20% off forever" and
"50% off your first month" are different offers, not different products or different stores. A per-tenant
or per-product setting could not express both at once, and both are ordinary things to want.

### Only a promotion can carry

The **storefront-wide discount (ADR-0053) never rides a renewal**, whatever else is true. It is a
point-of-sale setting — a sale the store is running today — not a term of the subscription the shopper
bought. Locking a transient 10% store-wide sale into a subscription forever, because someone happened to
subscribe during it, would make a temporary marketing lever permanently expensive and would surprise the
operator who set it.

So for a subscription line:

```
first period charged  = list − (every discount: promotions + store-wide)   [paid with the order]
every renewal charged = list − (only promotions flagged AppliesToRenewals)
```

Worked example — a 2000/month plan, a 25% permanent promotion, a 10% store-wide sale:

| | Amount |
|---|---|
| List | 2000 |
| Promotion −25% (flagged) | −500 |
| Store-wide −10% | −200 |
| **First period charged** | **1300** |
| **Every renewal charged** | **1500** |

### The price is fixed at purchase, not re-derived

The renewal-carrying discount is allocated per line at checkout and **persisted** on the order line
(`OrderLine.RenewalDiscountMinor`), then `SubscriptionRequested` carries `UnitPriceMinor − that`. Renewals
are priced from the terms the shopper actually bought under — not re-evaluated later against promotions
that may since have been edited, expired, or deleted. A shopper's price cannot change because marketing
changed its mind, and Payments needs no promotion context at renewal time (it has none).

Mechanically, `PromotionOutcome.RenewalLineDiscountsMinor` is the same largest-remainder allocation as
`LineDiscountsMinor`, restricted to the winning promotions that carry, and clamped element-wise to the
discount actually given — so the subtotal cap can never leave a renewal discounted by more than its own
first period was. When promotions stack, only the flagged half rides: an introductory promotion and a
permanent one can win together, and the renewal keeps only the permanent one.

## Consequences

* **Nothing changes for existing data.** The flag defaults false in the aggregate, in `PromotionChanged`
  (appended), in the projected copy, and in both new columns — so every promotion written before this ADR
  keeps charging list on renewal, which is what it already did.
* An order placed before this change has `RenewalDiscountMinor = 0` on every line (column default), so its
  renewals stay at list. No backfill, and no historical subscription silently repriced.
* The admin promotion form gains one checkbox, localized in all six languages. It is meaningless on a
  one-time product, and deliberately not hidden — a promotion can cover both kinds of line.
* **The storefront does not yet show the renewal price.** A shopper buying a discounted subscription sees
  what they pay today, and the "then 2000/month" line is not rendered anywhere. That is a real gap in
  "shown == charged" for the *second* period, and the follow-up worth doing next; this ADR deliberately
  fixes the money before the display, because the money was already being charged.
* Reporting that reads `Subscription.PriceMinor` now reads the *renewal* price, which for an introductory
  offer is higher than the first invoice. That is correct, and worth knowing before someone reconciles the
  two and reports a discrepancy.

## Verification

* `CouponTests` (unit) — an introductory promotion leaves the renewal vector zero; a flagged one carries
  its allocation; **only the flagged half of a stack rides**; and the renewal discount never exceeds the
  discount actually given, even when the subtotal cap bites.
* `SubscriptionRenewalPriceTests` (integration) — a real verified-member subscription checkout, end to end
  through the saga into Payments: introductory → `Subscription.PriceMinor` = 2000 (list) while the first
  period was charged 1500; flagged → 1500 forever; and **the store-wide discount never rides**, with the
  first period at 1300 and the renewal at 1500.
