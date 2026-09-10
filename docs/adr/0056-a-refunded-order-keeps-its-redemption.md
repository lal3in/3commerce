# 0056 — A refunded order keeps its redemption; an abandoned checkout does not

Status: Accepted — implemented (records existing behaviour)
Area: Ordering / pricing
Extends: [0052](./0052-coupon-codes-and-redemption-limits.md) (the redemption lifecycle this fixes the end of), [0051](./0051-threshold-promotions-and-combinability.md) (the promotion a coupon gates), [0055](./0055-discounted-refund-basis-and-storefront-scoped-tax.md) (the refund path that does *not* touch a redemption)

## Context

ADR-0052 defined three quarters of the redemption lifecycle precisely — reserved at checkout, confirmed
on `CheckoutCompleted`, released by the saga's single cancellation path — and said nothing about the
fourth: what happens to a **confirmed** redemption when the order it belongs to is later refunded,
partially refunded, disputed or charged back.

The code has always had an answer, and a sturdier one than "nobody calls it". Ordering's
`RefundCompleted`, `PaymentDisputed` and `PaymentChargedBack` consumers do not release anything — but
more importantly `PromotionRedemptionService.ReleaseAsync` is status-guarded **in SQL**
(`WHERE "OrderId" = … AND "Status" = 'Reserved'`), so releasing a *confirmed* redemption is a no-op even
if some future refund path calls it by mistake. Verified while writing this ADR: adding a
`ReleaseAsync` call to the `RefundCompleted` consumer changes nothing at all, and only widening the
guard to `IN ('Reserved', 'Confirmed')` actually breaks the policy. But nothing recorded
that this was a decision rather than an oversight — and an unwritten policy in a money path is
indistinguishable from a bug nobody has noticed yet. A pricing audit flagged exactly that: *possibly
correct, undocumented and untested, therefore unverifiable.*

## Decision

**A confirmed redemption is never released. A coupon is spent when the sale is made, and a later refund
does not un-spend it.**

The allowance rations *the discount*, not *the revenue*. The shopper received what the code promised: the
goods left at the discounted price, the promotion did its work, and the campaign's budget was genuinely
consumed. A refund reverses the money, not the fact that the offer was taken up.

Releasing instead would create a refund-shaped hole in every limited code: buy with the last redemption
of a single-use 50%-off code, refund, and the code is live again — repeatable, self-service, and
indistinguishable from ordinary returns activity. Rationing that a customer can reset on demand is not
rationing. That risk is concrete; the cost of the chosen policy is a shopper who returns their order and
cannot reuse a one-shot code, which is a discount question a human can answer case by case.

The distinction that matters is **whether the sale ever happened**, not whether it survived:

| Outcome | Redemption | Why |
|---|---|---|
| Payment fails / shopper cancels / 30-min expiry | **Released** | No sale was ever made; the hold was insurance on a charge that never landed |
| Crash strands the hold (45-min sweep) | **Released** | Residue of a half-committed reservation — nobody was ever charged |
| Order confirmed | **Confirmed** | The discount was granted |
| Order later refunded, partially refunded, disputed, or charged back | **Stays confirmed** | The discount *was* granted; reversing the money does not reverse that |

A reward worth nothing is the one case that looks like a sale but isn't: a free-shipping code on a cart
that pays no shipping saves the shopper zero, so no allowance is spent at all (it is never reserved —
`PromotionOutcome.ValuedPromotionIds`). That is a separate rule from this one, and it applies *before*
the sale, not after.

## Consequences

* Operationally, a refunded shopper who needs the code back needs a human — issue a new code, or raise
  `MaxRedemptions`. This is deliberate: it puts a person in front of the decision, which is where a
  discount exception belongs.
* Campaign reporting counts redemptions as *offers taken up*, which will exceed *sales that stood* by
  exactly the refunded orders. `PromotionCopy.RedeemedCount` is not a revenue figure and must not be
  read as one.
* A chargeback — where the shopper may be an adversary — likewise keeps the redemption spent. Handing an
  allowance back on a disputed payment would be the worst case of the refund-abuse hole above.
* Nothing changed in the code for this ADR. It records behaviour that was already correct and adds the
  tests that make it verifiable, so a future refactor that widens the release guard fails a test instead
  of quietly opening the hole.
* **The guard is the load-bearing part, not the call sites.** A reviewer looking to preserve this policy
  should watch `ReleaseAsync`'s `WHERE … "Status" = 'Reserved'`, not the list of consumers — a new refund
  path calling `ReleaseAsync` is harmless, while a well-meant "release regardless of status" is the whole
  hole in one line.
* If a tenant ever needs the opposite policy, it belongs on the promotion as an explicit
  `ReleaseOnRefund` flag rather than as a change to this default — that way the risky behaviour is
  something a human opted into, per campaign.

## Verification

`CouponRefundPolicyTests` (integration) drives a real order to `Confirmed` on a single-use coupon, then:

* a **full refund** → the redemption stays `Confirmed`, `RedeemedCount` stays 1, and the same shopper is
  refused the code a second time;
* a **chargeback** → identical;
* a **partial refund** → identical, with the order still `Confirmed`.

Contrast case, in `CouponRedemptionTests`: a checkout that never confirms (payment failure) *does*
release and the code becomes spendable again — so the two suites pin both halves of the rule and neither
can drift without the other noticing.

The full-refund case was confirmed to FAIL against a deliberately widened release guard, so it tests the
policy rather than merely describing it.
