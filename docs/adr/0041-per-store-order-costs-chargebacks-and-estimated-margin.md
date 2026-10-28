# 0041 — Per-store order costs, chargebacks, and estimated margin

Status: Accepted
Area: Payments / Financials
Extends: [0014](./0014-stripe-only-v1-double-entry-ledger.md) (double-entry ledger as truth), [0040](./0040-per-currency-per-storefront-ledger.md) (per-currency, per-storefront ledger), [0039](./0039-payment-provider-architecture.md) (per-PSP cash/fees)

## Context

ADR-0040 gave each storefront its own income accounts (`revenue.store-{id}`, `tax.store-{id}`, `shipping.store-{id}`) so the Financials page reports revenue, tax and shipping **per store** and **per currency**. But the *cost* side of an order was almost entirely invisible: buying a carrier label posted nothing, the COGS accrual (`SupplierPayable.ToAccrualEntry`) had no production caller, RMA dispositions moved no money, and chargebacks did not exist. So the by-storefront view showed income with no matching costs — a store looked 100% margin. This ADR records the decisions that made per-store **contribution margin** real.

## Decisions

1. **Cost accounts mirror the per-store income pattern.** New expense/liability accounts, each with a shared fallback and a per-store variant derived from the storefront id:
   - Carrier shipping cost → `expense.shipping_carrier` / `expense.shipping.store-{id}`, accrued against `liability.carrier_payable` when a label is bought (no cash moves; the carrier invoices later).
   - Cost of goods sold → `expense.cogs` / `expense.cogs.store-{id}`, accrued against `liability.supplier_payable` when an order is paid (the dormant `SupplierPayable` path, now wired: supplier cost originates on the Catalog `Offer`, rides `OfferChanged` into Ordering's `OfferCopy`, and is resolved at confirmation by the same `OfferResolution` that set the line's supplier — so cost and supplier can't drift). *Superseded in mechanism by ADR-0059 Amendment 2: the supply offer's unit cost is snapshotted on the line at checkout, the accrual books it and records `BookedSupplierCostMinor`, and RMA reversals reverse that booked figure.*
   - Inventory write-offs → `expense.writeoffs` / `expense.writeoffs.store-{id}`.
   - Chargeback fees → `expense.{provider}_chargeback_fees`, distinct from processing fees (`expense.{provider}_fees`).

2. **RMA dispositions correct the COGS accrual.** Support → Ordering (values the returned goods from the order's lines) → Payments. A **Restock** reverses the accrual (a restocked-then-resold unit re-accrues, so without the reversal it double-expenses); **Storage** reclasses it to a write-off (total expense unchanged, the loss surfaces on its own P&L line). Disposition edits carry a revision and reverse-then-repost; idempotent by `{RmaId}:{Revision}`.

3. **Chargebacks reverse the sale + book a fee.** A `ChargebackOpened` webhook reverses the remaining un-refunded sale through the store's own accounts (like a refund) and books the provider's dispute fee to `expense.{provider}_chargeback_fees`; the payment moves to `Disputed` and the order is badged. Financials shows chargeback fees on their own P&L line (excluded from the processing-fees line).

4. **No FX — deliberate, documented relabel.** There is no rate feed. A cost denominated in a currency other than the order's (e.g. the fake carrier always quotes AUD; an EUR-priced offer bought in an AUD order) is **relabelled** into the order/payment currency without conversion, with a one-per-order warning — the same pragmatic posture as the mock PSP settling into `cash.stripe`. A misleading 100%-margin (from *skipping* a foreign-currency cost) is worse dev data than an approximate cost. The Financials page reports strictly per currency; there is no cross-currency total. Revisit every relabel site together when an FX feed lands. *(ADR-0059 Amendment 2: COGS no longer relabels — the supply offer is always in the order's currency; an offer only in another currency supplies nothing. The carrier-cost relabel stands.)*

5. **Estimated overheads are configuration, never postings.** Per-storefront cost-assumption rates (packaging, labour, marketing, insurance, buffer — basis points) drive an *estimated* overhead/margin column in Financials, rendered visually distinct from posted figures. They are never written to the ledger: fake journal entries would corrupt the append-only books (0014). Real money movements stay in the ledger; assumptions stay a rendered overlay.

## Invariants & reconciliation

- Every journal line carries `(AccountCode, Currency)`; a balance is keyed by that pair. **A line MUST set its currency** — the COGS accrual originally built lines by hand and omitted it, so COGS dropped out of the per-currency P&L while still showing in the by-store column (the two disagreed). All postings now set line currency.
- The net-revenue line is guarded `if (netMinor > 0)` (like shipping/tax): a net-zero order (shipping-only, or a usage-metered product with no upfront price) must not post a zero line (violates the `ck_line_one_side` check constraint).
- Reconciliation identity, per metric per currency C: **Σ(each store's account, lines in C) + (shared/unattributed in C) = the per-currency P&L value in C.** "EUR-storefront COGS", "EUR-currency COGS" and "total COGS" coincide only when a single store in that currency accrued it; they diverge (legitimately) with multiple stores per currency, cross-currency relabels, or unattributed postings — but the identity always holds.

## Consequences

- Financials shows true per-store contribution margin (net revenue + shipping − COGS − carrier cost − write-offs), plus an estimated net margin after assumed overheads.
- COGS/carrier/write-off accruals are cash-neutral liabilities until a payout/settlement flow books them out — consistent with 0014's accrual posture.
- Storefront duplication (per [0040] account derivation) stays safe: a clone auto-derives fresh `*.store-{newId}` cost accounts too, so clone books never mix with the source's.

## Amendment (2026-10-15) — renewals and usage charges carry tax; their chargebacks are not modelled

A subscription renewal and a metered usage charge now post net revenue + output tax to the storefront's own accounts
(ADR-0057 amendment, ADR-0063 P4), so per-store net revenue no longer overstates inclusive-regime stores by their
GST/VAT. Chargebacks (this ADR) remain **order-payment only**: a dispute is matched to a `Payment` by payment intent,
and renewal/usage charges create none, so a dispute on one is recorded in the webhook inbox and otherwise ignored.
Modelling it needs a per-charge payment record; until then a renewal/usage dispute must be booked by hand.

## Amendment (2026-10-28) — renewal and usage charges are payments: refunds, partial refunds and disputes reach them

Owner decision 2026-10-27 (`renewal_usage_refund_path`): record a `Payment` per renewal and usage charge, so the
existing refund path, partial refunds and the dispute webhooks reach them, with the reversal taking the tax from that
charge's own ledger entry. This supersedes the 2026-10-15 amendment's "not modelled".

* **A payment per settled charge.** `Payment` gains `Kind` (`Order` = 1, every existing row; `Renewal` = 2;
  `UsageCharge` = 3), `ChargeReference` and `SubscriptionId` (migration `RecurringChargePayments`).
  * When a renewal or usage charge SETTLES, Payments books its sale entry (`renew-{subscriptionId}-{sequence}`,
    `overage-…`, `autoload-…`) and adds a `Payment` in the same unit of work (`Payment.ForSettledCharge`).
  * That payment carries the charge's intent id, the gross charged, the storefront and provider, and `ChargeReference` =
    the entry's reference.
  * Its `TaxMinor` is read from that entry (`Ledger.TaxOf`: the tax account's credit), so a reversal never re-derives
    tax from today's rate or from the subscription's price.
  * An unsettled charge records nothing, as it posts nothing. A renewal settled later by the provider's success event
    (3-D Secure) records its payment under that intent. A usage charge has no order (`OrderId` = empty).
* **One checkout payment per order, still.** The unique index on `OrderId` is now filtered to `Kind = 1`, because a
  renewal shares its subscription's order. `ChargeReference` is unique where set.
  * Every lookup that means "the order's payment" filters to `Kind = Order`: authorize, the carrier-cost accrual's
    currency, the subscription's copied instrument, and the refund path.
  * The LocalMock rail gives each usage charge its own intent (`pi_fake_charge_…`), as it already did for renewals,
    so a webhook matches exactly one payment.
* **Refunds.** `RefundRequested` appends `ChargeReference`. Null refunds the order's checkout payment as before. A
  reference refunds that charge; a usage charge needs no order id, and a named order must match.
  * `POST /admin/refunds` accepts `chargeReference`, and needs `orderId` or `chargeReference`.
  * `GET /admin/subscriptions/{id}` shows each settled renewal's `chargeReference`, intent, tax, refunded amount and
    payment and dispute status.
  * The single refund consumer applies the usual rules: the remaining-amount cap, the provider refund on the charge's
    own intent and settling provider, proportional tax and banker's rounding, and `Refunded` when fully refunded.
    The tax is the charge's own, so a full refund nets every account the charge touched back to zero, the tax account
    included, and partial refunds sum to exactly that tax.
* **Disputes.** The webhook processor matches by intent, as before. A charge payment's dispute reversal is booked
  under `{chargeReference}:chargeback` (`Payment.ChargebackReference`; an order keeps `{orderId}:chargeback`), and a
  win is booked under `…-reinstate`, so it never collides with the order's own dispute.
  * A charge payment's own lifecycle events (succeeded, failed, voided) post nothing and publish nothing: the payment
    exists only once the charge settled and was booked. This covers a redelivery and the success webhook every
    off-session charge also sends.
* **Ordering leaves the order alone.** `RefundCompleted`, `PaymentDisputed` and `PaymentChargedBack` append
  `ChargeReference`. Ordering ignores events that carry one: a refunded or disputed renewal is not a refunded or
  disputed order. The RMA saga stays order-level.
* **Not changed.** Xero still posts each refund by its refund id. Renewals settled before this change have no
  `Payment` row, and there are no live customers yet; refunding one of those is a manual journal.

Verification:
* `RecurringChargePaymentTests` (Payments unit, e2e-verify **A3t**) covers:
  * the payment per settled renewal, usage charge and 3-D Secure-settled renewal, with tax == the entry's;
  * no payment for an unsettled charge;
  * a refund by reference netting to zero, and partial refunds of 38 + 45 = 83, then refused;
  * an order refund never reaching a renewal, and a foreign or unknown reference refused;
  * a renewal dispute under its own reference, reinstated on a win;
  * a late success webhook for a refunded renewal posting nothing;
  * one mock intent per usage charge.
* `RecurringChargeRefundTests` (integration, **A4e**) runs the same paths on real Postgres + RabbitMQ: the filtered
  index, `POST /admin/refunds` with `chargeReference`, `/dev/simulate-chargeback` on a renewal intent, usage partial
  refunds, and an order that stays Confirmed and not disputed.
* Each fix was shown to fail its tests when reverted.
