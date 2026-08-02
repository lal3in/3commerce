# 0041 — Per-store order costs, chargebacks, and estimated margin

Status: Accepted
Area: Payments / Financials
Extends: [0014](./0014-stripe-only-v1-double-entry-ledger.md) (double-entry ledger as truth), [0040](./0040-per-currency-per-storefront-ledger.md) (per-currency, per-storefront ledger), [0039](./0039-payment-provider-architecture.md) (per-PSP cash/fees)

## Context

ADR-0040 gave each storefront its own income accounts (`revenue.store-{id}`, `tax.store-{id}`, `shipping.store-{id}`) so the Financials page reports revenue, tax and shipping **per store** and **per currency**. But the *cost* side of an order was almost entirely invisible: buying a carrier label posted nothing, the COGS accrual (`SupplierPayable.ToAccrualEntry`) had no production caller, RMA dispositions moved no money, and chargebacks did not exist. So the by-storefront view showed income with no matching costs — a store looked 100% margin. This ADR records the decisions that made per-store **contribution margin** real.

## Decisions

1. **Cost accounts mirror the per-store income pattern.** New expense/liability accounts, each with a shared fallback and a per-store variant derived from the storefront id:
   - Carrier shipping cost → `expense.shipping_carrier` / `expense.shipping.store-{id}`, accrued against `liability.carrier_payable` when a label is bought (no cash moves; the carrier invoices later).
   - Cost of goods sold → `expense.cogs` / `expense.cogs.store-{id}`, accrued against `liability.supplier_payable` when an order is paid (the dormant `SupplierPayable` path, now wired: supplier cost originates on the Catalog `Offer`, rides `OfferChanged` into Ordering's `OfferCopy`, and is resolved at confirmation by the same `OfferResolution` that set the line's supplier — so cost and supplier can't drift).
   - Inventory write-offs → `expense.writeoffs` / `expense.writeoffs.store-{id}`.
   - Chargeback fees → `expense.{provider}_chargeback_fees`, distinct from processing fees (`expense.{provider}_fees`).

2. **RMA dispositions correct the COGS accrual.** Support → Ordering (values the returned goods from the order's lines) → Payments. A **Restock** reverses the accrual (a restocked-then-resold unit re-accrues, so without the reversal it double-expenses); **Storage** reclasses it to a write-off (total expense unchanged, the loss surfaces on its own P&L line). Disposition edits carry a revision and reverse-then-repost; idempotent by `{RmaId}:{Revision}`.

3. **Chargebacks reverse the sale + book a fee.** A `ChargebackOpened` webhook reverses the remaining un-refunded sale through the store's own accounts (like a refund) and books the provider's dispute fee to `expense.{provider}_chargeback_fees`; the payment moves to `Disputed` and the order is badged. Financials shows chargeback fees on their own P&L line (excluded from the processing-fees line).

4. **No FX — deliberate, documented relabel.** There is no rate feed. A cost denominated in a currency other than the order's (e.g. the fake carrier always quotes AUD; an EUR-priced offer bought in an AUD order) is **relabelled** into the order/payment currency without conversion, with a one-per-order warning — the same pragmatic posture as the mock PSP settling into `cash.stripe`. A misleading 100%-margin (from *skipping* a foreign-currency cost) is worse dev data than an approximate cost. The Financials page reports strictly per currency; there is no cross-currency total. Revisit every relabel site together when an FX feed lands.

5. **Estimated overheads are configuration, never postings.** Per-storefront cost-assumption rates (packaging, labour, marketing, insurance, buffer — basis points) drive an *estimated* overhead/margin column in Financials, rendered visually distinct from posted figures. They are never written to the ledger: fake journal entries would corrupt the append-only books (0014). Real money movements stay in the ledger; assumptions stay a rendered overlay.

## Invariants & reconciliation

- Every journal line carries `(AccountCode, Currency)`; a balance is keyed by that pair. **A line MUST set its currency** — the COGS accrual originally built lines by hand and omitted it, so COGS dropped out of the per-currency P&L while still showing in the by-store column (the two disagreed). All postings now set line currency.
- The net-revenue line is guarded `if (netMinor > 0)` (like shipping/tax): a net-zero order (shipping-only, or a usage-metered product with no upfront price) must not post a zero line (violates the `ck_line_one_side` check constraint).
- Reconciliation identity, per metric per currency C: **Σ(each store's account, lines in C) + (shared/unattributed in C) = the per-currency P&L value in C.** "EUR-storefront COGS", "EUR-currency COGS" and "total COGS" coincide only when a single store in that currency accrued it; they diverge (legitimately) with multiple stores per currency, cross-currency relabels, or unattributed postings — but the identity always holds.

## Consequences

- Financials shows true per-store contribution margin (net revenue + shipping − COGS − carrier cost − write-offs), plus an estimated net margin after assumed overheads.
- COGS/carrier/write-off accruals are cash-neutral liabilities until a payout/settlement flow books them out — consistent with 0014's accrual posture.
- Storefront duplication (per [0040] account derivation) stays safe: a clone auto-derives fresh `*.store-{newId}` cost accounts too, so clone books never mix with the source's.
