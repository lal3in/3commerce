# 0040 — Per-currency, per-storefront ledger

Status: Accepted (2026-07-25)
Context: Ledger multi-currency / multi-storefront program (PRs #106–#110 + per-store refunds/receivable). Extends ADR-0014 (custom double-entry ledger as source of truth) and builds on ADR-0038 (per-currency shelf prices) and ADR-0039 (provider-agnostic payments).

## Context

ADR-0014 established a custom, append-only double-entry ledger in Payments as the source of truth, with provider-scoped cash/fee accounts (`cash.{provider}`, `expense.{provider}_fees`) and shared revenue/tax accounts (`revenue.sales`, `revenue.refunds`, `liability.tax_collected`). Two gaps surfaced once the platform ran multiple storefronts in different currencies (ADR-0038 gives each storefront its own currency):

1. **Currency was entry-level only.** A `JournalEntry` carried a `Currency`, but a single account (e.g. `cash.stripe`) accumulated postings in several currencies. Any *balance* on that account summed non-comparable minor units (EUR + AUD + USD), so no account total or trial balance was meaningful.
2. **No per-storefront books.** All sales credited the shared `revenue.sales`, so a tenant running several storefronts could not see revenue, tax, or a P&L per storefront.

## Decision

**Currency is a first-class ledger dimension.** `JournalLine` carries its own `Currency` (denormalized from the entry). Every balance, trial balance, and report groups by `(AccountCode, Currency)` and is **never summed across currencies**. The trial balance holds **per currency** (Σ debits = Σ credits within each currency). There is **no FX consolidation** — a single-currency consolidated view would require an exchange-rate feed we don't have, so each currency stays its own report.

**Each storefront owns its accounts.** A `Storefront` carries `ReceivableAccountCode` / `RevenueAccountCode` / `TaxAccountCode`, auto-derived from the storefront id (`{kind}.store-{id}` — the full id, since a short UUIDv7 prefix is a shared creation-time prefix that collides across stores) and editable at create/update. **Cash stays per settling provider+currency** (`cash.{provider}` + the line currency), because money settles per PSP, not per storefront.

**Sales and refunds post to the storefront's accounts via a receivable settlement bridge.** When a payment is storefront-attributed:

- **Sale:** `Dr receivable.{store}` (gross) / `Cr revenue.{store}` (net) + `Cr tax.{store}` (tax); then `Dr cash.{provider}` (gross) / `Cr receivable.{store}` (gross). The receivable nets to zero when cash settles with the sale and carries a balance only while a settlement is outstanding.
- **Refund:** reverses the store's *own* revenue/tax through the same receivable bridge, cash out on the settling provider.
- **No-storefront (legacy / subscription) postings are unchanged** — cash-basis against the shared `revenue.sales` / `revenue.refunds` / `liability.tax_collected`.

**Propagation.** `StorefrontId` rides `AuthorizePayment` → `Payment`; the first-party storefront app sends it in the checkout body (authoritative for attribution) over the gateway's host-derived `X-3C-Storefront-Id` (which collapses path-based demo stores to one default). Catalog publishes each storefront's account codes on `StorefrontConfigChanged`; Payments keeps a local `StorefrontLedgerAccounts` projection (ADR-0008) and resolves the codes by the payment's `StorefrontId` at posting time.

**Reporting.** A new admin **Financials** page renders a per-currency P&L (Revenue − Refunds − Fees) and financial position (cash by provider + receivable = assets; tax = liabilities), plus a by-storefront breakdown. Refunds are detected by the refund journal entry (revenue-account debits on a `Refund …` entry), so both shared and per-store refunds are counted.

## Consequences

- Balances and P&L are trustworthy per currency and per storefront; the Ledger page shows a balanced block per currency.
- Existing single-currency, non-storefront flows are byte-for-byte unchanged (defaults preserve the ADR-0014 postings); the ledger's append-only + per-entry-balance invariants (NFR-1) still hold.
- **Deferred:** FX/consolidation (needs a rate feed); per-store checkout currency is driven by the cart currency the storefront app sends (already per-store via ADR-0038 pricing), so no separate mechanism is required.
