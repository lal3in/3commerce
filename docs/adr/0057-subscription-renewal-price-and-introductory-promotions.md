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
* **The storefront shows the renewal price** (closed by the immediate follow-up). `GET /cart/summary`
  reports `renewals` — the recurring lines' cost per period from the next one on, grouped by billing
  period — and the cart and checkout both render it ("Renews monthly at ..."). The figures exclude every
  discount that does not ride renewals, so an introductory deal cannot be mistaken for the ongoing price.
  This extends "shown == charged" to the SECOND invoice, which is where a subscription's real cost lives.
  <br/>Note for anyone touching that code: billing mode is read with `OfferCopy.ResolveOffer` (since ADR-0059 Amendment 2: `ResolveLineOffer`) — the
  resolver CHECKOUT uses — and not with `ResolvePricingOffer`, which ignores an offer carrying no price of
  its own and would silently report no renewal at all for exactly the subscriptions that need one.
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
  first period at 1300 and the renewal at 1500. The same suite pins the PREVIEW: an introductory cart
  showing 1300 today reports 2000 as the ongoing price, a permanent promotion reports its own 1500, and a
  one-time cart reports no renewal at all.

## Amendment (2026-10-15) — a renewal posts its tax (ADR-0063 P4, `renewal_usage_tax_2026_10_11`)

`Subscription.PriceMinor` is a **shelf price** (ADR-0038), so it means different things in different regimes. Until
this amendment every renewal (and every metered usage charge) was posted as if it carried no tax: on an inclusive
store (AU GST / EU VAT) the whole charge landed in `revenue.store-{id}` and output tax was under-reported.

* **What is snapshotted, what is not.** At purchase `SubscriptionRequested` now carries the line's tax *terms*
  (appended, optional): `TaxInclusive` (the storefront's regime — does the price contain the tax?) and `TaxExempt`
  (the line was destination-exempt for the order's ship-to via `ProductShipRule`, the lookup checkout used). Payments
  stores both on the subscription. The **rate is not snapshotted**: rates change by law and each period is a new
  supply, so a renewal uses the storefront's rate **in force at the charge**, projected into Payments'
  `StorefrontLedgerAccounts` from `StorefrontConfigChanged` (`TaxRateBasisPoints`, `TaxInclusive`). This matches
  ADR-0063 §7.1.
* **The split is checkout's.** `RecurringChargeTax` extracts `amount × bps / (10000 + bps)`, half away from zero —
  unit-swept equal to `CheckoutEndpoints` for every amount 0..20000 at nine rates — and the posting is the usual
  receivable-bridged `Ledger.Sale` with net to `revenue.store-{id}` and tax to `tax.store-{id}`. **The charged gross
  never changes.** 0% stores and exempt lines post no tax line (`ck_line_one_side`); an unprojected rate (a
  projection written before this change, or no storefront) posts no tax and logs a warning — never a guess.
* **Exclusive regimes are unchanged, pending an owner decision.** *(Superseded the same day by the owner decision
  below: exclusive renewals and usage charges now add the tax.)* Checkout ADDS tax on an exclusive (US) store, but
  a renewal has always charged the bare `PriceMinor` — and the cart preview ("renews at x") shows that bare figure.
  Adding tax would change what customers are charged, so it is not done silently: renewals and usage on an
  exclusive store still charge and post exactly as before (a warning per charge). `RecurringChargeTax.ExclusiveTaxOn`
  already pins checkout's add-on arithmetic so the decision is a call-site change.
* **Refunds.** A renewal or usage charge creates no `Payment` row, so the refund saga (keyed by the order's payment)
  and the dispute webhook (keyed by the payment intent) cannot reach one: there is no renewal/usage refund or
  chargeback path to reverse its tax yet. When one is built it must reverse the tax portion of the `renew-{id}-{seq}`
  entry (`Ledger.Refund` with that tax nets every account to zero — unit-pinned).

Verification: `RecurringChargeTaxTests` (unit, e2e-verify A3h) and `RenewalUsageTaxTests` (integration, A4b).

## Amendment (2026-10-15) — owner decision: exclusive regimes ADD the tax to renewals and usage (option A, `renewal_usage_tax_exclusive_decision`)

The owner chose **option A** of the three put to them (A: add the tax to the charge and say "+ tax" in the preview;
B: keep charging the bare price, the merchant owes the uncollected tax; C: keep the charge but treat it as
tax-inclusive, the merchant absorbs the tax). On a tax-**exclusive** storefront (US sales tax, ADR-0038) a
subscription renewal and a usage charge (overage and prepaid auto-load) are now charged exactly as checkout charged
the first period:

* **The charge.** `RecurringChargeTax.For(price, rate, inclusive: false, exempt)` returns basis `Exclusive` with
  `tax = price × bps / 10000` rounded half away from zero (checkout's formula — unit-swept equal for every amount
  0..20000 at six rates) and `ChargeMinor = price + tax`. The tax is resolved **before** the provider call
  (`SubscriptionService.RenewalTax`, `UsageChargeBilling.ChargeAndPostAsync`) and the rail is asked for
  `ChargeMinor`; the idempotency key is unchanged (`renew-{id}-{periodEnd}` / the usage reference). The period's
  history row (`SubscriptionRenewal.AmountMinor`) records what was charged.
* **The amount is pinned to its idempotency key.** Because the tax now changes the charge, a retry of the same key
  after the store's rate changed would send a different amount — and a real provider rejects a reused key with
  different parameters (Stripe `idempotency_error`), so that renewal or usage charge would fail permanently. The
  first attempt therefore computes price + tax once and **persists** it (`RecurringChargePin`: an
  `IdempotencyRecord` under `charge-amount:{key}`, committed **before** the provider call, so even a crash between
  the charge and our save replays the same figures). Every retry of that key — the same period re-run after a
  failure, a redelivered usage message — reuses the pinned amount and split verbatim: same key, same amount, so
  the provider de-duplicates instead of rejecting or double-charging, and the posting matches what was charged.
  A redelivery after success is stopped earlier still by the journal-reference check. The **next** period is a new
  key and takes the rate in force then.
* **Each renewal commits on its own (deliberate).** A nightly run (`AutoRenewDueAsync`) used to commit every renewal
  in one final `SaveChanges`; a crash or DB failure late in the run then rolled back the record of charges that had
  already moved money. Now each renewal is its own unit of work: pin committed → provider call → the period's
  outcome (advanced + history row + ledger entry, or PastDue) committed — before the next customer's card is
  touched. A run can lose at most the renewal in flight, which re-runs under its pinned key and amount (the provider
  de-duplicates). The commit sits outside the dunning catch, so a failure to *record* a successful charge aborts the
  run for a retry instead of dunning a customer who paid. A newly created billing schedule is committed explicitly
  too, so the pin's commit never carries other pending changes. Pinned by
  `RecurringChargeAmountTests.A_nightly_run_commits_each_renewal_before_the_next_provider_call_and_a_failed_one_stays_retryable`
  (another connection sees renewal 1 committed at renewal 2's provider call; a provider failure on renewal 2 duns
  only it; renewal 3 commits; renewal 2 later retries with the same key and amount).
* **The posting.** The usual receivable-bridged `Ledger.Sale` with **gross = the charge**, **tax = the added tax**
  (to `tax.store-{id}`) and therefore **net = the price** (to `revenue.store-{id}`) — the split checkout posts.
  Every line carries the currency; a tax that rounds to 0 posts no tax line (`ck_line_one_side`).
* **What adds nothing.** A 0% store, a line destination-exempt at purchase (`Subscription.TaxExempt`) and an
  inclusive store charge the price itself, as before. A subscription bought before the terms snapshot
  (`TaxInclusive = null`) follows the storefront's projected regime — so an existing US subscriber's next renewal
  starts adding tax, matching what their first period was charged at checkout.
* **Unknown rate — charge the bare price, never guess, never refuse.** When Payments has no projected rate for the
  store (a `StorefrontLedgerAccounts` row written before `TaxRateBasisPoints` was carried, or a charge with no
  storefront), the renewal/usage charge goes ahead at the **bare price** with no tax posted. The alternatives were
  worse: charging a guessed rate could over-charge a customer beyond what they were told; refusing or deferring the
  charge would dun (or silently stop billing) a customer for a gap in *our* read model, and a never-arriving
  projection would mean free service indefinitely. Under-collection is bounded to the tax, it is logged as an
  **error** on an exclusive store (a warning on an inclusive one, where only the split is missing) naming the store,
  and it heals the moment the storefront's config is re-published (saving the storefront in admin re-sends
  `StorefrontConfigChanged`). A future refinement could snapshot the purchase-time rate as a fallback.
* **What the customer sees.** `CartRenewalResponse` gains `TaxAdded` (appended boolean `taxAdded`, default false, so
  an older client simply ignores it): true when the storefront is exclusive with a non-zero rate and the period's recurring lines are not
  all destination-exempt for the shopper's known ship-to. The storefront then renders the renewal as
  **"{amount} + tax"** in the cart and at checkout (all six locales) instead of a bare figure — consistent with how
  checkout itself shows exclusive tax (prices before tax, a separate "Tax (added)" line), and honest because the
  renewal uses the rate **in force at the renewal**, which the preview cannot promise. Inclusive stores are
  unchanged (the amount *is* the charge). There is no renewal or usage email that states an amount, and no storefront
  account page shows the next charge, so nothing else changes; the admin Subscriptions history shows the charged
  amount per period.
* **Not changed here.** Renewal/usage refunds and chargebacks (`renewal_usage_refund_path` — still no `Payment` row;
  when built, `Ledger.Refund` with the entry's tax reverses it exactly — unit-pinned for the exclusive shape too);
  `subscription_renew_on_failed_charge`; `subscription_renewal_price_quantity` (the renewal price ignores line
  quantity while the preview uses the line total — tax is added on whichever price Payments charges, so that
  mismatch carries through unchanged).

Verification: `RecurringChargeTaxTests` + `RecurringChargeAmountTests` (unit — a recording provider pins the amount
the rail is asked for; e2e-verify A3h + A3l), `RenewalUsageTaxTests` (integration, A4b) and
`e2e/renewal-plus-tax.spec.ts` (Playwright, L20).

## Amendment (2026-10-27) — owner decisions: only a settled renewal renews; a renewal is priced for the whole line

Two defects found by `renewal_usage_tax_2026_10_11` and decided by the owner on 2026-10-27.

### An unsettled renewal is dunned (`subscription_renew_on_failed_charge`)

`SubscriptionService` advanced the period whenever the provider *returned*, whatever it returned: only a thrown error
dunned to PastDue. A **Failed** or **RequiresAction** outcome therefore opened a new period (history row and all)
while the ledger — which posts only on Succeeded — recorded no revenue, so the subscription and the ledger
disagreed and the customer got a free period.

* **Decision.** Only a **Succeeded** charge renews. Failed and RequiresAction (and Cancelled, which also took no
  money) go down the **existing dunning path**, exactly like a thrown provider error: the subscription becomes
  **PastDue**, the period does **not** advance, no `SubscriptionRenewal` row is written, and **no revenue or tax is
  posted**. The outcome is committed per renewal as before (2026-10-15 amendment), with a warning log naming the
  outcome and error code.
* **The retry is the same period.** Because the period did not advance, the next attempt computes the same key
  `renew-{id}-{periodEnd:O}` and `RecurringChargePin` replays the same pinned amount — even after a rate change — so
  the provider sees one charge, not two. The existing retry is the operator's **Renew** (`POST
  /admin/subscriptions/{id}/renew`, or the admin Subscriptions page): the nightly auto-renew only picks Active /
  Trialing subscriptions, so it never re-charges a past-due one. **Follow-up decided (owner, 2026-10-27,
  `subscription_past_due_retry_and_authenticate`, branch `feature/subscription-dunning-retries-and-pay-page`):** the
  nightly job retries a PastDue subscription on days 1, 3 and 7 after the first failure, then cancels it and emails
  the customer; each retry uses a FRESH attempt key (`renew-{id}-{periodEnd:O}-a{n}`) with the SAME pinned amount,
  because Stripe replays a reused key's cached result for about 24 hours; and a storefront "complete payment" page
  lets the customer finish 3-D Secure or update the card, linked from the RequiresAction and dunning emails.
* **RequiresAction asks the customer.** No dunning notification existed, so an additive contract
  `SubscriptionRenewalActionRequired` (tenant, subscription, storefront, email, the pinned amount, the period end) is
  published through the bus outbox — it commits with the dunning, or not at all — and the Notifications worker's
  `SubscriptionRenewalActionRequiredConsumer` (a unique consumer name, ADR-0060) emails the customer that their bank
  needs them to confirm the renewal, that nothing was charged, and where to sign in. The delivery-log reference is
  `subscription-renewal-action:{subscriptionId}:{periodEnd}`. A plain decline sends no email (unchanged).
* **Usage charges.** `UsageChargeBilling` already posted nothing for an unsettled charge and has no "settled" mark of
  its own (the journal entry is the de-dupe), so it was not affected in Payments; it now logs the unsettled outcome
  and a re-sent charge under the same reference charges the same pinned amount once. The Usage service, however,
  marks the overage billed / credits the prepaid auto-load when it *publishes* the charge, so a declined usage charge
  is forgiven there. **Follow-up decided (owner, 2026-10-27, `usage_charge_failure_feedback`, delivered with tax
  P4b's settled-charge events):** Payments publishes settled / failed per usage charge, and Usage reverses the
  auto-load credit or marks the overage unpaid on failure, with the same retry and email rules as renewals.

### A renewal is priced for the whole line (`subscription_renewal_price_quantity`)

`OrderStatusConsumer` published `UnitPriceMinor − RenewalDiscountMinor` as the subscription price. The renewal
discount is allocated per **line**, while the price was per **unit**, so a quantity-2 line renewed at one unit's
price less the whole line's discount (2 × 1000 with a 25% permanent promotion previewed 1500 but would renew at 500),
and the cart's "renews at" preview (`line.TotalMinor − RenewalLineDiscountsMinor[i]`) disagreed with the charge.

* **Decision.** A renewal = **unit × quantity − the line's renewal discount** (`OrderLine.RenewalPriceMinor()`,
  never negative) — the preview's figure and the basis of the first charge. Quantity > 1 stays allowed.
* **Carried, not re-derived.** `SubscriptionRequested.PriceMinor` is now that line total, and the contract appends
  `Quantity` (default 1). Payments stores it on `Subscription.Quantity` (migration `SubscriptionQuantity`, existing
  rows default to **1**); it is informational — `PriceMinor` already covers every unit, so every downstream reader of
  the price (renewal charge, tax, the admin API) is unchanged in shape. The admin API appends `quantity` to
  `SubscriptionDto` / `SubscriptionDetailDto`.
* **Existing subscriptions are not repriced.** A subscription set up before this change was priced for one unit and
  keeps renewing at that (quantity 1) until it is backfilled. **Follow-up decided (owner, 2026-10-27,
  `subscription_quantity_backfill`, branch `fix/subscription-quantity-backfill`):** an idempotent operator command
  in the ADR-0060 backfill tool (`tools/order-confirmed-backfill`), with a dry-run mode, re-sends each existing
  quantity > 1 subscription's quantity and renewal price from its order line to Payments.
* **Traced.** *Tax:* `RecurringChargeTax` works on `PriceMinor`, so an exclusive store adds its rate on the line total
  — for a single line the same tax checkout added to the first period (pinned end to end: 2 × 1000 − 25% → 1500 +
  124). *Preview:* unchanged — it already showed the line total, and now equals the stored price. *Second storefront
  sharing the currency:* the subscription carries its own storefront and that store's rate; nothing is shared.
  *Refunds:* a renewal still has no `Payment` row (`renewal_usage_refund_path` — decided as the next follow-up: a
  `Payment` row per renewal and usage charge so the refund saga, partial refunds and disputes reach it, the reversal
  taking that charge's own ledger tax); the order's own refund is untouched.
  *Usage:* overage is billed per unit of usage by the Usage service, independent of subscription quantity.

Verification: `SubscriptionRenewalOutcomeTests` (Payments unit), `SubscriptionRenewalQuantityTests` (Ordering unit)
and `SubscriptionRenewalActionEmailTests` (Notifications, unit lane) — e2e-verify **A3n**; `SubscriptionRenewalDunningTests`
(integration — a real 2-unit checkout through to the renewal, and a RequiresAction renewal delivered over RabbitMQ
through the outbox) — **A4c**. Each was proven to fail with its fix reverted.

### Follow-up delivered — the quantity backfill (`subscription_quantity_backfill`, 2026-10-27)

The ADR-0060 operator tool gains a `subscription-quantity` command (`tools/order-confirmed-backfill`, see its README).
It selects a recurring order line of quantity > 1 only when Payments still holds its subscription at quantity 1 **and**
exactly the pre-fix price (`unit − the line's renewal discount`); anything else is reported, never sent (a subscription
that already carries a quantity, none, cancelled, or a quantity-1 subscription at some other price). For each selected
line it **sends** the live `SubscriptionRequested` (`OrderStatusConsumer.SubscriptionRequestFor`, with the
subscription's own snapshotted tax terms) to Payments' `subscription-requested` queue, after the usual broker
preflight, with a deterministic message id; `--dry-run` / `--execute` is mandatory. Payments applies it in
`SubscriptionService.StartAsync` through `Subscription.CorrectLegacyQuantity`: the only change an existing
subscription accepts from a request, and only from quantity 1 to a larger quantity — never a downgrade, never a
second correction — so a redelivery or a re-run is a no-op. A period already attempted keeps its pinned amount; the
corrected price applies from the next period key. **Precondition (owner, 2026-10-27): run it before live customers
exist** — production is in Stripe test mode with no real customers, so no customer notice is sent; once live customers
exist, a notice of the higher renewal must be sent first. Verification: `SubscriptionQuantityBackfillTests` (tool, e2e-verify
A3e), `SubscriptionTests` (domain), `SubscriptionFlowTests` (integration, A6g).

### Follow-up delivered — dunning retries and the "complete payment" page (`subscription_past_due_retry_and_authenticate`, 2026-10-27)

* **The schedule.** The nightly auto-renew (per storefront, at its daily run time) now also retries PastDue
  subscriptions: retry k falls due once `DunningStartedAt + [1, 3, 7][k-1]` days have passed and nothing was attempted
  since (`Subscription.ScheduledRetryDue`) — a customer's own attempt on the pay page counts, and several missed days
  collapse into one retry. When the **day-7** retry does not settle, the subscription is **cancelled** and
  `SubscriptionCancelledForNonPayment` emails the customer. A settled retry renews once and clears the dunning state.
  `DunningStartedAt` is the period's first failure; subscriptions already past due when this shipped start the
  schedule at the deployment (migration `SubscriptionDunning`), never with an immediate final retry.
* **Fresh attempt keys, one pinned amount.** Every charge of a period is a `SubscriptionChargeAttempt` row (attempt n,
  its own provider key `renew-{id}-{periodEnd:O}` for n = 0 and `…-a{n}` after, the trigger, the intent id, the
  outcome) committed **before** the provider call. An attempt the provider ANSWERED (Failed / RequiresAction /
  Cancelled) is followed by a fresh key, because a provider replays a reused key's cached result (Stripe: about
  24 hours). An UNANSWERED attempt (the call threw, or a crash) is retried under its own key, so the provider
  de-duplicates instead of charging twice — and it neither emails nor uses up a scheduled retry. The amount stays
  pinned to the period (`RecurringChargePin` keyed by the period key), whatever the rate does in between.
* **What the customer is told.** RequiresAction → `SubscriptionRenewalActionRequired`; a decline →
  `SubscriptionPaymentFailed`; both now carry `NextRetryAt` and `CancelsAfter`, and the emails link the pay page
  (`/account/subscriptions/{id}/pay`). The final failure → `SubscriptionCancelledForNonPayment`. Attempts the customer
  makes on the page send no email (they see the answer there).
* **The pay page.** `GET /me/subscriptions/{id}` (the customer's own; 404 otherwise) returns the amount due (the pinned
  charge), the reason, the next retry and the cancellation date. `POST /me/subscriptions/{id}/pay` (Idempotency-Key
  required; replays return the stored answer and never the client secret) charges the period **on-session**
  (`PaymentRequest.CustomerPresent` → Stripe `off_session = false`, so the bank may ask for 3-D Secure), optionally on
  another of the customer's saved cards — which then becomes the card future renewals charge. Outcomes cross as numbers
  (1 Paid, 2 RequiresAction + intent id + client secret, 3 Declined, 4 ProviderUnavailable); 409 when nothing is due,
  422 for a card that is not the customer's. The storefront page renders the reason, amount, schedule and a card
  choice; on RequiresAction it shows the authentication step — today the test-mode step (the storefront has no card
  SDK yet, exactly like checkout's test payment), in production the client confirms the intent with its client secret.
* **Settling after 3-D Secure.** A renewal attempt has no `Payment` row, so the provider's `payment_intent.succeeded`
  for its intent is routed by `PaymentEventProcessor` to `SubscriptionService.SettleAttemptAsync`: renew once, post the
  pinned split, mark the attempt settled — idempotent (inbox + the attempt's `SettledAt` + the journal reference). Money
  that arrives for a period that is already closed (settled by another attempt, or cancelled) is **not** booked; it is
  logged as an error naming the intent for a manual refund (a renewal refund path is `renewal_usage_refund_path`).

Verification: `SubscriptionDunningTests` + the updated `SubscriptionRenewalOutcomeTests` (Payments unit) and the
dunning emails in `SubscriptionRenewalActionEmailTests` — e2e-verify **A3p**; `SubscriptionRenewalDunningTests`
(integration, **A4c**: the day 1/3/7 schedule with fresh keys ending in a cancellation delivered over RabbitMQ, the HTTP
pay endpoint with its idempotency, and a 3-D Secure attempt settled once from the provider's success event);
`e2e/subscription-pay-page.spec.ts` (Playwright, **L20**: the account lists the past-due subscription, the page shows
the reason and amount, paying makes it active again). Each fix was shown to fail its tests when reverted.

### Follow-up delivered — renewal and usage refunds and disputes (`renewal_usage_refund_path`, 2026-10-28)

Each settled renewal and usage charge is now a `Payment` of its own (kind `Renewal` / `UsageCharge`, keyed by the
journal reference that booked it, carrying its intent id and the tax that entry booked), so `POST /admin/refunds`
(`chargeReference`), partial refunds and the dispute webhooks reach it; a full refund nets the charge's tax account to
zero, and Ordering leaves the order alone. Details and verification: ADR-0041, amendment 2026-10-28.

### Follow-up delivered — usage charges report back and are dunned (`usage_charge_failure_feedback`, 2026-10-28)

Delivered with ADR-0063 P4b on `feature/settled-charge-events`.

* **Payments records and duns every usage charge.** `UsageChargeBilling` (now a scoped service) records the request once
  per reference (`UsageCharge`, unique `Reference`), pins the amount, and makes each attempt a `UsageChargeAttempt`
  committed before the provider call — exactly the renewal machinery: the retry rules live in one place,
  `DunningSchedule` (days 1, 3, 7; `Subscription` now delegates to it), an ANSWERED failure is followed by a fresh key
  `{reference}-a{n}`, an unanswered one (the provider threw) duns silently as `Error` and keeps its key, and the amount stays
  pinned. A redelivered request never re-charges an answered charge (retries are the schedule's). The new job
  `usage-charge-retry` (hourly) runs `RetryDueAsync`; the final failure **abandons** the charge. The provider's later
  success event for a usage attempt (routed by `PaymentEventProcessor` after the renewal check) settles it once — even
  when abandoned, because the usage was consumed and the money is owed.
* **Behaviour change (accepted): a provider exception no longer redelivers the request.** Before, a provider call that
  threw propagated out of the usage consumer, so MassTransit's retry redelivered the message (and an exhausted retry left
  it in the error queue). Now, exactly as for renewals, the unanswered attempt marks the charge past due with reason
  `Error`, publishes nothing (no customer-visible failure, Usage keeps its credit) and is retried by the dunning schedule
  under the SAME key, so the provider de-duplicates it. A failure to RECORD a settled charge still throws and is redelivered.
* **Outcomes are published.** Settled → `UsageChargeSettled` (P4b facts). Every answered failure → `UsageChargeFailed`
  (`Reason` Failed / RequiresAction / Cancelled, `Attempt`, `NextRetryAt`, `AbandonsAfter`, `Final`).
* **Usage reacts.** It records each charge it publishes (`usage.UsageCharges`, Open → Settled / Unpaid / Abandoned,
  migration `UsageChargeOutcomes`) and consumes both events (`UsageBalanceChargeSettledConsumer`,
  `UsageBalanceChargeFailedConsumer` — unique names, ADR-0060). The **first** failure of an open auto-load takes back its
  optimistic credit (never below zero); an overage stays billed-but-unpaid (never billed twice). The balance gets a
  `PaymentHold`: **Unpaid** while a charge is retried (no further auto-load top-ups — a declining card is not charged
  again; usage beyond the allowance and the prepaid credit needs overage to be allowed), **Suspended** once a charge is
  abandoned (billable usage refused; the allowance and remaining prepaid credit still work). A settlement re-grants a
  taken-back credit and releases the hold when nothing else is unpaid. An outcome for a charge published before this
  change is recorded on arrival and applied the same way.
* **The customer is told.** Notifications' `UsageChargeFailedEmailConsumer` emails each failure (what was not charged, that
  an auto-load's credit is on hold, the next retry and the suspension date) and the final one (billable usage suspended);
  delivery-log reference `usage-charge-failed:{reference}:{nextRetry|final}`.
* **Defect fixed on the way.** An overage reference was `overage-{balance}-{level}`; the level resets every period, so a
  repeated level in a later period reused an earlier, already posted reference and was never charged. It is now
  `overage-{balance}-{periodStartUnix}-{level}`.
* **Deferred.** A usage "complete payment" page (no storefront usage UI exists; the emails link `/account`) — tracker
  `usage_charge_pay_page`. Charging the customer's stored instrument for usage charges (they still go to the host
  default account with no customer/payment method) stays open in `jobmgr_3_settlement`.

Verification: `UsageChargeDunningTests` + `SettledChargeEventTests` (Payments), `UsageChargeFeedbackTests` (Usage),
`SettledChargeContractTests` (emails, contracts) — e2e-verify **A3u**; each was shown to fail with its fix reverted.
