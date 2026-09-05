# Coupon codes at checkout — execution plan (ADR-0052)

Branch: `feat/coupon-codes` · Base: `0107e7d` (PR #257, threshold promotions) · ADR:
[0052](../../docs/adr/0052-coupon-codes-and-redemption-limits.md)

## Goal

A shopper enters a code at checkout and gets its discount; the tenant can cap how many times a code is
redeemed in total and per customer. **Shown == charged**, and a limited code can never be over-redeemed —
not even by simultaneous checkouts.

## Non-goals

Per-code analytics surfaces, bulk/generated code batches (one code per shopper), referral codes, gift
cards (a stored-value instrument, not a discount rule), "you're $X away" nudges. All follow-ups.

## The shape of the change

A coupon is **not a new entity**. It is the ADR-0051 `Promotion` with an optional `Code`, so the whole
existing pipeline — threshold, reward, window, storefront scope, combinability, the shared
`PromotionEvaluator`, the largest-remainder per-line allocation, the tax base — is reused verbatim. Only
two concepts are genuinely new: *"applies only when the shopper enters this code"* and *"may only be
spent N times"*.

The hard part is **when** the count binds. The charged amount and the payment authorization are both
fixed at checkout, so the redemption is **reserved there**, before the payment intent is requested —
never at confirmation, which would let two shoppers both be charged a "last one" price.

## Phases

| # | Phase | What lands |
|---|---|---|
| 1 | Catalog | `Promotion.Code` + `MaxRedemptions` + `MaxRedemptionsPerCustomer`, validated setters, filtered unique index, migration, `PromotionChanged` fields, `/admin/promotions` create+update, domain facts |
| 2 | Ordering read model | `PromotionCopy` coupon fields + Ordering-owned `RedeemedCount`, projection consumer, `PromotionEvaluator` coupon gate, `CouponValidator`/`CouponStatus` |
| 3 | Redemption | `PromotionRedemption` entity, `PromotionRedemptionService` (reserve / confirm / release + stale sweep), migration |
| 4 | Checkout + cart | `POST /checkout` `couponCode` + reservation before authorization; `GET /cart/summary` `couponCode` + numeric status; confirm/release hooks on `OrderStatusConsumer` |
| 5 | Storefront | `CouponBox` apply/remove, validated-code-only submission, eight localized reasons × 6 locales |
| 6 | Admin | Code + Redemptions columns, coupon fieldset, `GET /admin/promotion-redemptions` in Ordering, 19 keys × 6 locales |
| 7 | Test/Docs | unit + integration (incl. the concurrency case) + Playwright, ADR-0052 + index, API index + OpenAPI, `services.html`, `e2e-verify.sh` + its COVERAGE CHECKLIST, this tracker |

## The race, and how it is won

```sql
UPDATE ordering."PromotionCopies"
   SET "RedeemedCount" = "RedeemedCount" + 1
 WHERE "PromotionId" = @id
   AND ("MaxRedemptions" IS NULL OR "RedeemedCount" < "MaxRedemptions")
```

One statement; rows-affected is the answer. Postgres row-locks for its duration, so concurrent checkouts
serialize and the loser's predicate re-evaluates against the winner's increment. A read-then-write does
not hold, which is why there is a counter column rather than a `COUNT(*)`. The per-customer limit has no
row to serialize on, so its count is preceded by a transaction-scoped advisory lock on
`(promotionId, customerKey)`. Both sit inside one explicit transaction with the redemption insert, whose
unique `(PromotionId, OrderId)` makes the reservation idempotent per order.

## GOTCHAs carried into execution

- `dotnet format` the Infrastructure csproj after EVERY `ef migrations add`.
- Money is integer minor units; `Net + Ship + Tax = Gross` and trial balance = 0 must hold.
- `CheckoutResponse` / `CartSummaryResponse` / `CheckoutRequest` are positional records — APPEND with defaults.
- Enums cross HTTP as NUMBERS (`CouponStatus` included).
- Publish BEFORE `SaveChangesAsync` (the outbox trap).
- The projection consumer must NOT assign `RedeemedCount`, or a re-publish resets the cap.
- After an admin-page change, re-run the admin specs (a shifted column has broken a sibling spec before).
- Don't `npm run build` against the `.next` the dev server is serving.

## Validation

Unit (aggregate + evaluator gate + every refusal reason) · integration (charged, **cap under concurrent
checkouts**, per-customer, release on failed payment, idempotent confirm, trial balance 0) · fresh-DB
migrations with no pending model changes · Playwright (storefront apply/remove + admin authoring) ·
`dotnet build` + `dotnet format --verify-no-changes` · storefront `tsc`/`lint` · six-locale parity.
