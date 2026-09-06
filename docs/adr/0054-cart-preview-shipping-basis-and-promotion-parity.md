# 0054 — The cart preview's shipping basis: quote it, or say it isn't decided yet

Status: Accepted — implemented
Area: Ordering / Storefront / Fulfillment / pricing
Operator/handbook view: [`docs/help/pricing-and-promotions.md`](../help/pricing-and-promotions.md) §2.7 + §3.2 — how the preview decides, and what a shopper sees when it cannot
Extends: [0051](./0051-threshold-promotions-and-combinability.md) (the shared `PromotionEvaluator` and the `discount + freeShippingValue` benefit rule), [0028](./0028-product-supply-profiles-composable-supply.md) (the carrier-rate seam the quote comes from), [0050](./0050-per-country-ship-rules-and-ship-to-allowlist.md) (`shippingCovered` waives the rate outright), [0052](./0052-coupon-codes-and-redemption-limits.md) (a coupon is a promotion, so it enters the same contest)

## Context

ADR-0051 scores a promotion by **customer benefit** = `discountMinor + (freeShipping ? shippingMinor : 0)`
and picks the better of `[best single exclusive]` vs `[Σ combinables]`. Checkout knows `shippingMinor`:
the selected carrier quote, or 0 for a non-shippable / collect-at-warehouse / ship-rule-covered cart.

`GET /cart/summary` did not. It passed `CheckoutEndpoints.FlatShippingMinor` — a flat **499** — because no
carrier quote exists that early. That guess is the defect:

> Cart 100.00, two exclusive promotions — **free shipping**, and **9.00 off**.
> At the guessed 499 the cash discount wins (900 > 499). At a real rate of 1500 free shipping wins.
> Same cart, same promotions, **two different winners**. The shopper is shown one reward and charged
> under the other.

The goods discount is computed identically either way and the charge is never wrong — but "shown ==
charged", the property ADR-0051 exists to guarantee, was silently false in exactly the case promotions are
most visible. It was recorded as a known limitation in the handbook rather than fixed.

Three facts shaped the fix.

1. **The preview usually does know the shipping amount — or knows it is zero.** A cart with no
   shipping-requiring line pays nothing whatever a carrier says (ADR-0028 `RequiresShipping` + the
   tenant's `ProductType` policy), and a cart whose ship rules cover shipping pays nothing either
   (ADR-0050). And a shipping address — required for anything physical — makes a real quote possible,
   for a **guest** exactly as for a signed-in shopper.
2. **Where the amount is genuinely unknown, the outcome usually does not depend on it anyway.** Benefit
   is linear in the shipping amount with slope 0 (cash) or 1 (free shipping), so the winner changes at a
   handful of crossing points — and in most carts there is no crossing at all.
3. **A guess is worse than an admission.** A preview that commits to a winner it cannot know is a
   correctness bug; one that says "confirmed once we know where this is going" is merely honest.

## Decision

1. **The preview resolves shipping in checkout's own order, and never invents a rate.**

   ```
   1. no line requires shipping (all digital/service)        → 0
   2. every line's ship rule covers shipping for the country → 0
   3. the rate quoted for the shopper's destination          → that amount
   4. otherwise                                              → UNKNOWN
   ```

   Cases 1–2 are computed inside Ordering from the same `OfferCopy` / `ProductTypeShippingPolicyCopy` /
   `ProductCopy.ShipRules` read copies checkout uses — the gate now lives in one shared
   `CartShipping.LineRequiresShipping`, called by both. **`FlatShippingMinor` is no longer a preview
   input.**

2. **`GET /cart/summary` takes the quoted rate as `?shippingMinor=` (+ `?shipToCountry=`).** It is a
   DISPLAY input only: shipping is charged from the quote **checkout itself** validates (service, expiry,
   revalidation), so a client that sends a nonsense rate only mis-informs itself. The value is clamped to
   a sane range before it is scored. This mirrors how checkout already receives
   `SelectedShippingAmountMinor` from the client rather than re-quoting server-side.

3. **The storefront does the quoting, against the same endpoint and with the same request.** The cart and
   checkout pages resolve a destination — the address a shopper already entered (kept in an HttpOnly
   session cookie) or their saved default; **authentication is not a gate** — and call
   `POST /api/fulfillment/shipping/quote` with the shared origin/parcel constants the checkout rate picker
   uses, taking `rates[0]` (the rate that picker preselects). The storefront is the right place because it
   is the only tier that holds both the session (for saved addresses) and the entered form values;
   Ordering has neither, and would have to grow a synchronous dependency on Identity to get them.

4. **Quotes are cached per (storefront, cart contents, destination) for 5 minutes.** The cache key IS the
   quote's input, so a changed cart or a changed address simply misses — there is no stale rate to
   invalidate. A carrier failure yields *no rate*, not an error: the preview falls back to decision 5 and
   the cart still renders.

5. **When the amount is unknown, the evaluator measures the whole range instead of guessing.**
   `PromotionEvaluator.Preview` probes the selection at shipping `0` and at `subtotal + 1` and returns a
   basis:

   | Basis | Meaning | What the shopper sees |
   |---|---|---|
   | `Settled` | both probes agree ⇒ the same winner at **every** shipping amount | today's rows, decided |
   | `Quoted` | the winner depends on shipping, and shipping is known | today's rows, decided |
   | `Provisional` | the winner depends on shipping, and shipping is unknown | the **floor**, plus "free shipping may apply at checkout" |

   Two probes suffice, and this is a proof rather than a sample: every benefit line has slope 0 or 1, so
   the exclusive branch's upper envelope is convex and its argmax switches from cash to free-shipping at
   most once; the combinable set is a single line, so `C − E` is monotone and also flips at most once.
   Every crossing solves `d₁ + S = d₂` with `d₂ ≤ subtotal`, so all of them lie in `[0, subtotal]` —
   probing `0` and `subtotal + 1` straddles them all, and equal probes prove the outcome is constant in
   between. A 400-trial property test sweeps random promotion mixes against every rate and asserts it.

6. **A provisional preview reports the guaranteed floor and never asserts free shipping.** At shipping 0
   the winner maximises the goods discount, so the high-shipping outcome always has the *smaller* goods
   discount — that is the floor, and it is what the preview shows. `FreeShippingApplied` is reported
   `false` (it is not decided); the basis is what tells the storefront to render "free shipping may
   apply". The consequence is a one-directional guarantee: **the preview never promises more than the
   charge delivers**, and everything that could improve is labelled as such.

7. **Checkout is untouched and remains authoritative.** It keeps `PromotionEvaluator.Evaluate` and the
   real rate. Both entry points now share a single `Candidates()` step (eligibility + threshold
   measurement), so a preview probes exactly the candidate set checkout will select from and the two can
   differ *only* on the shipping amount.

8. **The checkout page re-prices when the shopper changes the shipping option.** Free shipping is worth
   exactly the rate it waives, so upgrading to express can legitimately hand the contest to a different
   promotion. The rate picker re-fetches the summary for the newly selected rate, so the last thing the
   shopper sees before paying is the verdict checkout will apply — not the one that won at a rate they
   have since changed.

## Alternatives considered

- **Keep a flat fallback, but a better-calibrated one.** Cheap and wrong for the same reason: any constant
  is right for some carts and wrong for others, and the failure is silent. Rejected.
- **Make the preview shipping-independent — always resolve the goods discount at shipping 0 and show free
  shipping as a separate "may apply" note.** Honest about free shipping, but it *over-promises the goods*:
  the shipping-0 winner has the largest goods discount, so a shopper shown "−10.00 off items" could be
  charged under a free-shipping promotion that takes only 6.00 off. Rejected in favour of the floor
  (decision 6), which errs in the shopper's favour instead.
- **Have Ordering fetch a carrier quote itself on every cart view.** It has no destination (addresses live
  in Identity, behind the shopper's session), so it would need a synchronous Identity call *and* a
  Fulfillment call on every cart render — for a value the storefront already holds. Rejected.
- **Always mark the preview provisional whenever free shipping is in play.** Simple, but it hedges the
  overwhelmingly common case where the winner cannot change, training shoppers to ignore the wording.
  Rejected — the range probe is what makes the hedge rare and therefore meaningful.
- **Show a range ("you'll save 6.00–10.00").** More information than a shopper wants on a cart line, and
  it still needs the same range computation underneath. Rejected; the floor plus one sentence carries the
  same guarantee.

## Consequences

- **`GET /cart/summary` gains `?shippingMinor` + `?shipToCountry` and returns `basis`** (a NUMBER on the
  wire, platform invariant). `CartSummaryResponse` is a positional record, so the field is appended with a
  default — an older client deserializes `Settled` and behaves exactly as before.
- **Two new shopper-facing strings**, localized across all six `messages/*.json`:
  `cart.freeShippingMaybe` / `checkout.freeShippingMaybe` and `cart.provisionalNote`.
- **The cart page may now make up to two extra calls** (saved addresses when a session exists, and one
  carrier quote) — both skipped for a shopper with no known destination, and the quote is cached for 5
  minutes per cart+destination. A carrier outage degrades to the provisional path, never to an error.
- **The known limitation recorded in ADR-0052's "known gap" style and in the handbook is retired.** The
  remaining honest gap is narrower and now explicit in the UI: a shippable cart with no address anywhere,
  where the preview says so rather than guessing.
- **No money invariant moved.** `Net − Discount + Ship + Tax = Gross`, the subtotal cap, the
  largest-remainder per-line allocation (`Σ LineDiscountsMinor == DiscountMinor`) and the trial balance of
  0 are all unchanged — the preview only ever chooses which genuine `Select` outcome to display.
- Tested by `PromotionPreviewTests` (basis semantics + the floor property + a 400-trial sweep),
  `PromotionMatrixTests` (twelve preview-vs-charge integration cases across the discount × promotion ×
  shipping space, each asserting the money identity and a trial balance of 0), and
  `promotion-shipping-parity.spec.ts` (the provisional wording, and the cart settling on the real rate
  once an address is entered).
