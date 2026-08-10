# 0045 — Mandatory per-storefront ledger attribution (no shared/default accounts)

Status: Accepted
Area: Payments / ledger / accounting
Extends: [0014](./0014-stripe-only-v1-double-entry-ledger.md) (double-entry ledger as truth), [0040](./0040-per-currency-per-storefront-ledger.md) (per-currency, per-storefront income accounts + receivable bridge), [0041](./0041-per-store-order-costs-chargebacks-and-estimated-margin.md) (per-store cost accounts), [0043](./0043-storefront-scoped-carriers-payments-and-go-live-gate.md) (storefront-scoped settings cloned on duplication)

## Context

ADR-0040 gave each storefront its own **income** accounts and ADR-0041 its own **cost** accounts — but each of those account roles still kept a **shared/default fallback** (`revenue.sales`, `liability.tax_collected`, `cash.{provider}`, `expense.{provider}_fees`, `expense.{provider}_chargeback_fees`, `revenue.refunds`, `expense.cogs`, `expense.shipping_carrier`, `expense.writeoffs`, `liability.supplier_payable`, `liability.carrier_payable`). A movement without storefront context — and several code paths *had* no storefront context: cash + fees on every sale, the refund/chargeback contra, and the supplier/carrier **payable** credit sides — landed on those shared accounts. That reintroduces exactly what the per-storefront model set out to remove: a platform-level pool where one store's money mingles with another's.

The directive: **there is no platform/tenant default. Every ledger movement must be attributed to a specific storefront.** Storefronts already clone their banking, carriers and payment settings on duplication (ADR-0043), so a full per-storefront chart is the natural extension.

## Decisions

1. **Every account role has a per-storefront code; the whole chart is per store.** Added the remaining builders (`Accounts.*StoreFor`) so cash + processing/chargeback fees are per **(storefront, provider)** — `cash.store-{id}.{provider}`, `expense.store-{id}.{provider}_fees`, `…_chargeback_fees` — and refunds-contra, COGS, write-offs and supplier/carrier payables are per store — `revenue.refunds.store-{id}`, `expense.cogs.store-{id}`, `expense.writeoffs.store-{id}`, `liability.supplier_payable.store-{id}`, `liability.carrier_payable.store-{id}`. Income codes stay operator-configurable via the Catalog projection (`StorefrontLedgerAccounts`); the rest are **derived deterministically** from the storefront id — `JournalLine.AccountCode` is a free string with no chart FK, so a posting computes its codes directly and no runtime provisioning is needed.

2. **The hot money paths route per storefront.** `Ledger.Sale`/`Refund`/`Chargeback` take the storefront id and settle cash, processing fees, chargeback fees and the refund/chargeback contra to the store's own accounts (`PaymentEventProcessor` + `ExecuteRefundConsumer` pass `payment.StorefrontId`). The COGS accrual and its RMA reversal (`SupplierPayable.ToAccrualEntry`, `Ledger.CogsReversal`) and the carrier-label accrual (`Ledger.CarrierCost`) route **both legs** — expense *and* the payable liability — per store, driven by the `StorefrontId` the accrual consumers already carried. Metered usage carries its storefront too (`UsageBalance.StorefrontId`, stamped onto the `UsageOverageCharge`/`UsageAutoLoadCharge` contracts).

3. **Forward-only.** Historical entries that predate attribution keep their shared codes — the ledger is append-only and audited (ADR-0014), so nothing is rewritten. The shared constants remain **only** as the fallback for genuinely unattributed (null-storefront) postings.

4. **The invariant is enforced by a guard test, not a runtime throw.** `Accounts.SharedCodes` / `Accounts.IsSharedCode` enumerate the shared/default codes; `NoSharedAccountPostingTests` drives every attributed factory and asserts no line lands on a shared code. A hard runtime throw on a live money path was rejected as too risky — the test locks the invariant for every attributed path while the fallback stays safe for the residual unattributed ones.

5. **Admin surfaces the per-storefront chart.** Payments exposes `GET /admin/ledger/storefronts/{id}/chart` — the authoritative derivation of a store's full chart (income from the projection, the rest derived) — and the admin Financials page adds a per-storefront chart-of-accounts view. The by-storefront liability figures now match every per-store payable (`StartsWith`), not just the shared code.

## Consequences

- A store's trial balance nets to zero on its **own** accounts; no attributed movement touches a shared pool. The by-storefront P&L and financial position are complete and non-overlapping across stores.
- Two posting gaps remain, tracked separately: subscription **renewals** and usage **overage/auto-load** charges only authorize the card today — they persist no `Payment` and post no ledger entry, so they record no revenue yet. The `StorefrontId` is already threaded onto their paths so the eventual posting lands per store with no further plumbing.
- No migration for the derived codes (they are strings, not rows); the only schema change is the nullable `UsageBalance.StorefrontId` column.
