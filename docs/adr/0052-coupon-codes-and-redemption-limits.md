# 0052 — Coupon codes at checkout: a code-gated promotion with race-safe redemption limits

Status: Accepted
Area: Catalog / Ordering / pricing
Extends: [0051](./0051-threshold-promotions-and-combinability.md) (the `Promotion` aggregate, the shared `PromotionEvaluator`, the combinability rule and the normative pricing order — all reused verbatim), [0047](./0047-storefront-scoped-active-window-offer-price.md) (the offer-resolved base a coupon discounts), [0041](./0041-per-store-order-costs-chargebacks-and-estimated-margin.md) (no FX — a coupon is currency-pinned), [0008](./0008-database-per-service-single-postgres.md) (read-copy projection; checkout never queries Catalog), [0045](./0045-mandatory-per-storefront-ledger-attribution.md) (no new ledger line — the charged gross simply drops), [0007](./0007-masstransit-rabbitmq-outbox-sagas.md) (the cancellation path that releases a hold)

## Context

ADR-0051 delivered promotions that apply **automatically** to any qualifying cart, and closed with
coupon-code gating and redemption caps as explicit follow-ups. Two facts shaped how they land.

1. **The engine already spoke "coupon".** `Pricing.cs` carried `PromotionKind.CouponFixed` /
   `CouponPercent`, a `CouponCode` on both `PricingInput` and the engine's `Promotion` record, the
   `OrdinalIgnoreCase` eligibility match and the discount arithmetic — all of it engine-only and reachable
   by no production caller. The gap was never the maths. It was that no *stored* promotion could carry a
   code, nothing projected one into Ordering, no endpoint accepted one from a shopper, and nothing counted
   how often a code had been spent.

2. **A coupon is not a new kind of thing.** Everything a coupon needs beyond a promotion — a window, a
   threshold, a percentage or fixed reward, a storefront scope, a combinable flag, per-line allocation —
   already exists on `Promotion`. The only genuinely new concepts are *"applies only when the shopper
   enters this code"* and *"may only be spent N times"*.

There is one further fact that dictates the hard part. **The charged amount and the payment
authorization are fixed at checkout**, not at confirmation. A "last one" 50%-off code that is counted
only when the order confirms can be authorized against two shoppers' cards at the discounted price and
honoured for one of them. Whatever rations a coupon has to bind at the moment the price is struck.

## Decision

1. **A coupon IS a promotion — `Promotion.Code`.** The ADR-0051 aggregate gains one optional, nullable
   `Code`. Set ⇒ the promotion is **code-gated** and applies only when the shopper enters that code.
   Null/empty ⇒ **automatic**, exactly today's behaviour, unchanged. No second entity, no parallel
   discount pipeline, no `CouponKind` enum. The code is normalized on write to **trimmed UPPERCASE**
   (1–40 chars of `A-Z 0-9 - _`) and matched case-insensitively, so `welcome10`, `Welcome10` and
   `WELCOME10` are one code and the admin table, the database and the shopper's entry all agree.

2. **Uniqueness is a database guarantee.** A **filtered** unique index on `(TenantId, Code) WHERE Code IS
   NOT NULL` means one code resolves to exactly one promotion, and two concurrent creates cannot both
   win it. The filter matters: an unfiltered unique index would allow only a single *automatic*
   (code-less) promotion per tenant. The endpoint probes first purely to turn a `23505` into an
   operator-readable 400.

3. **A code-gated promotion may carry NO threshold.** ADR-0051 required at least one threshold, because
   an automatic promotion without one applies to every cart unconditionally. For a coupon the **code is
   the gate**, and "10% off, no minimum, with code WELCOME10" is the commonest coupon there is. The
   invariant is therefore conditional, and *clearing* the code of a thresholdless coupon is refused —
   it would silently turn into a store-wide sale.

4. **Usage limits: `MaxRedemptions` and `MaxRedemptionsPerCustomer`.** Both nullable; null = unlimited;
   ≥ 1 when set (a zero limit is a promotion that can never apply — deactivating it is the honest way to
   say that). **Single-use is simply `MaxRedemptions = 1`** — no separate flag.

5. **`PromotionChanged` carries all three, appended with back-compatible defaults**, and the
   `PromotionCopy` projection consumer assigns them on both the insert and the update branch — like every
   other field. Migrations land in **both** services.

6. **Ordering owns the redemption**, because Ordering is where a coupon is *spent*. A new
   `PromotionRedemption` (`PromotionId`, `TenantId`, `OrderId`, `CustomerKey`, `Code`, `Status`,
   `ReservedAt`/`ConfirmedAt`/`ReleasedAt`) records each use, with a **unique index on (PromotionId,
   OrderId)** and an index on `(PromotionId, CustomerKey)`. `OrderId` is the single id checkout mints for
   both the `CheckoutAttempt` and the `Order` it becomes, so one column serves as both keys.

7. **`PromotionCopy.RedeemedCount` is Ordering-owned state, NOT projected.** It is the counter the cap
   guard increments, and the projection consumer deliberately never assigns it — a re-projection or a
   redelivered `PromotionChanged` must not reset a cap and hand a limited code out all over again.

8. **RESERVE AT CHECKOUT — the lifecycle.**

```
checkout   ── coupon validates AND wins ──▶  Reserved     (before AuthorizePayment)
Reserved   ── CheckoutCompleted          ──▶  Confirmed    (spent for good)
Reserved   ── OrderCancelled             ──▶  Released     (count given back)
              (payment failed · explicit cancel · saga 30-min expiry all publish it)
```

   The reservation is taken **after the order id is minted and before the payment intent is requested**,
   so a discount we cannot honour never reaches the provider. It is taken **only when the coupon actually
   won**: a coupon out-competed by a better promotion (ADR-0051 decision 8) discounted nothing, so it must
   not burn an allowance. The `RequestTimeoutException` path releases immediately, because no saga would
   ever start to do it. Confirm and release are status-guarded `UPDATE`s, so a redelivered message is a
   no-op — the messaging idempotency invariant.

9. **How the cap is made race-safe.** The total cap is enforced by **one conditional statement** whose
   rows-affected is the answer:

```sql
UPDATE ordering."PromotionCopies"
   SET "RedeemedCount" = "RedeemedCount" + 1
 WHERE "PromotionId" = @id
   AND ("MaxRedemptions" IS NULL OR "RedeemedCount" < "MaxRedemptions")
```

   Postgres takes a row lock for its duration, so concurrent checkouts serialize on that row and the
   loser's predicate is re-evaluated against the winner's increment. Zero rows affected = refused. A
   read-then-write ("count the rows, then insert") does **not** hold under concurrency, which is the whole
   reason a counter column exists rather than a `COUNT(*)`. The per-customer limit has no single row to
   serialize on, so its check is preceded by a **transaction-scoped advisory lock** keyed by
   `(promotionId, customerKey)` — the same mutual exclusion, over a key instead of a row. Both run inside
   one explicit transaction with the redemption insert.

10. **`CustomerKey` = `u:{userId}` when signed in, else `e:{trimmed, lowercased email}`.** A guest
    checkout still counts, and signing out (or opening a fresh browser) does not reset a per-customer
    limit. The prefix keeps the two namespaces from ever colliding.

11. **A second-chance sweep keeps the cap self-healing.** A crash between the reservation's commit and the
    checkout attempt's would otherwise strand a hold forever. When a claim is refused on a *capped*
    promotion, reservations older than the saga's expiry window (45 min) that have **no** checkout attempt
    and **no** order are released and the claim retried. A live checkout is never disturbed.

12. **Distinct, localized refusal reasons.** `CouponStatus` (crossing HTTP as a **number**, per the
    platform invariant) reports exactly one of: `UnknownCode`, `Inactive`, `NotStarted`, `Expired`,
    `WrongStorefront`, `ThresholdNotMet`, `UsageLimitReached`, `CustomerLimitReached`, `Applied`. Two
    ordering decisions matter: the code is looked up by `(tenant, code)` **without** the
    storefront/active/window filters, so a real code aimed at another store says so instead of "unknown"
    (which would send the shopper hunting a typo that isn't there); and the usage limits are checked
    **before** the threshold, because telling someone to spend more on an exhausted code is a lie. A
    currency mismatch reports `WrongStorefront` — under the no-FX posture (ADR-0041) a EUR coupon
    genuinely cannot apply to an AUD cart, and from the shopper's seat that is the same complaint.

13. **Shown == charged, extended to the coupon.** `GET /cart/summary` takes `couponCode`, runs the SAME
    validation checkout runs and prices the cart with it, returning the numeric status, the code and the
    promotion's name. It **validates but never reserves** — a preview must not consume an allowance;
    checkout's atomic reservation stays the sole authority on the limits. `POST /checkout` takes
    `CouponCode` and a code that does not apply is a **400 naming the reason**, never a silent
    full-price charge: the shopper was shown a discounted total, and charging more than was shown is the
    worst possible direction to break in.

14. **The storefront submits only a validated code.** The checkout page carries the code in the URL
    (`/checkout?coupon=…`) so the server can price with it, the state survives a refresh and "remove" is
    a link back to `/checkout`. Only a status of `Applied` rides the checkout POST as a hidden field; a
    refused code renders its own localized reason and is left out, so the charge always equals the total
    on screen. The 400 remains the guard for non-first-party clients and for a code that runs out between
    the preview and the POST.

15. **Stacking is not a new concept.** Once unlocked, a coupon is a promotion, so the existing
    `Combinable` flag governs whether it stacks — and the ADR-0051 better-of(`best exclusive`,
    `Σ combinables`) selection is untouched.

## Alternatives considered

- **A separate `Coupon` aggregate with its own table, contract, projection and evaluation.** Rejected:
  every field but two already exists on `Promotion`, and a second discount pipeline would need its own
  stacking rule, its own per-line allocation and its own tax-base story — the exact divergence ADR-0051
  spent a whole phase eliminating.
- **Reserve at confirmation instead of checkout.** Simpler (one hook, no release path), and wrong: the
  price is authorized at checkout, so a cap enforced later can be exceeded by concurrent checkouts that
  were all charged the discount. Rejected — this is the core of the ADR.
- **Enforce the cap with `COUNT(*) < Max` before inserting.** Rejected: two transactions both read the
  old count under READ COMMITTED and both insert. The conditional `UPDATE` is what actually holds.
- **`SERIALIZABLE` isolation for the reservation.** Correct, but it turns a hot coupon into a
  serialization-failure retry storm across an endpoint that also talks to the payment provider. The row
  lock is narrower and needs no retry loop.
- **A TTL on the reservation instead of an explicit release.** Rejected as the primary mechanism: the saga
  already publishes exactly one terminal cancellation event, so an explicit release is precise and
  immediate. The time-based sweep is kept only as a backstop for a crash between two commits.
- **Reusing the dormant `PromotionKind.CouponFixed`/`CouponPercent` engine kinds as the stored shape.**
  Rejected: those kinds have no threshold, no window, no scope and no projected copy. They stay as the
  engine's legacy vocabulary; the stored coupon is a `Threshold` promotion with a `Code`.

## Consequences

- **No new ledger line and no new account.** A coupon lowers the charged gross exactly as a promotion
  does, so the sale posts less revenue and the trial balance stays 0 (asserted in the integration cases).
  ADR-0045 per-storefront attribution is untouched.
- **`CheckoutRequest`/`CheckoutResponse` and `CartSummaryResponse` gain appended optional fields** — all
  three are positional records, so new fields must always be appended with defaults.
- **`CheckoutAttempt`/`Order` snapshot the redeemed `CouponCode`**, so a past order can be explained (and
  supported) without joining the redemption table.
- **The admin Promotions page grows a Code column, a Redemptions column and a coupon fieldset.** The
  usage counts come from Ordering's `GET /admin/promotion-redemptions` and are joined in the UI — two
  read APIs, never a cross-service DB query. "Used" counts **reserved** redemptions as well as confirmed
  ones, because a reservation is already unavailable to anyone else.
- **A guest's per-customer limit can only be reported at checkout, not on the preview.** `GET /cart/summary`
  is rendered before the shopper has typed an email, so for an anonymous cart there is no `CustomerKey` to
  count against and the preview reports the coupon as applied. Checkout — where the email exists — then
  refuses with `CustomerLimitReached`. For a signed-in shopper the preview is exact, because the user id is
  known. Closing the gap for guests would mean re-pricing on every keystroke of the email field; the
  narrow case (a guest re-using a per-customer-limited code from a fresh browser) is accepted instead, and
  the charge is still never wrong — only the warning is late.
- **An unlimited coupon still records a redemption row.** Usage reporting works, and a limit tightened
  later starts from the truth rather than from zero.
- **The reservation commits in its own transaction, before the checkout attempt's.** That is what makes
  the cap authoritative at the moment of pricing; the cost is the narrow crash window that decision 11's
  sweep covers.
- Out of scope, and still follow-ups: per-code analytics/reporting surfaces, bulk/generated code batches
  (one code per shopper), referral codes, gift cards (a stored-value instrument, not a discount rule), and
  "you're $X away" nudges.
