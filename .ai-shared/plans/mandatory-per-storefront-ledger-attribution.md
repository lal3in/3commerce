# Feature: Mandatory per-storefront ledger attribution (eliminate shared/default accounts)

The most money-sensitive change in the system. **Invariant to enforce:** every ledger movement is attributed
to a specific storefront — there is NO shared/platform/tenant-default account. Preserve the balanced
double-entry + per-line currency invariants ([[3commerce-ledger-posting-invariants]]) and the
format-verify-all-projects gate ([[3commerce-format-verify-all-projects]]).

Supersedes the smaller tracked follow-up `pay_renew_storefront_ledger` (renewal revenue only).

## Decisions (from Q&A)
- **Auto-provision at go-live**: every storefront gets a full per-storefront chart of accounts when it goes
  live; the codes clone on storefront duplication (which already clones banking/carrier/payment settings,
  ADR-0042/0043). A posting therefore always finds its accounts — no runtime fallback.
- **Forward-only**: new postings must be attributed; historical shared-account entries are NOT rewritten
  (an append-only audited ledger).
- **Fully per-storefront**: revenue, refunds-contra, tax, shipping income, receivable, cash, processing fees,
  chargeback fees, COGS, supplier payable, carrier payable — accruals AND payouts. Consistent with the
  per-storefront duplication of banking/carrier/settings.
- **Admin portal updated** to surface the per-storefront chart.

## Today (what must change)
- `StorefrontLedgerAccounts` (Payments read model, fed by Catalog `StorefrontConfigChanged` via
  `StorefrontLedgerConfigConsumer`) holds only **Receivable/Revenue/Tax/Shipping** per storefront, and its own
  doc says *"Absent projection → falls back to the shared accounts."*
- `Ledger.cs` factories (`Sale`/`Refund`/`Chargeback`/`CarrierCost`/`CogsReversal`/`Writeoff`/…) each have a
  `string.IsNullOrWhiteSpace(x) ? Accounts.<Shared> : x` fallback to the shared constants
  (`revenue.sales`, `revenue.refunds`, `shipping.income`, `liability.tax_collected`, `cash.{provider}`,
  `expense.{provider}_processing_fees`, `expense.{provider}_chargeback_fees`, `expense.cogs`,
  `liability.supplier_payable`, `expense.shipping_carrier`, `liability.carrier_payable`).
- Posting sites that lack storefront context today: subscription **renewals** (OrderId=Guid.Empty, no store),
  **usage** overage/auto-load charges (`UsageBalance` has no `StorefrontId`), and supplier/carrier accruals
  that pass no store accounts.

## CONTEXT REFERENCES — READ FIRST
- `src/Services/Payments/Domain/Ledger/LedgerAccount.cs` — the shared constants + `CashFor`/`ProcessingFeesFor`/
  `ChargebackFeesFor(provider)`. Introduce per-storefront code builders (e.g. `revenue.store-{id}`,
  `cash.store-{id}.{provider}`, `expense.store-{id}.{provider}_processing_fees`, …).
- `src/Services/Payments/Domain/Ledger/StorefrontLedgerAccounts.cs` — the read model to EXTEND to the full chart.
- `src/Services/Payments/Domain/Ledger/Ledger.cs` — remove every shared fallback; require resolved codes.
- `src/Services/Payments/Infrastructure/PaymentEventProcessor.cs` — sale/chargeback posting (already resolves
  `StorefrontLedgerAccounts` by `payment.StorefrontId`; must stop tolerating null).
- `src/Services/Payments/Infrastructure/Consumers/StorefrontLedgerConfigConsumer.cs` +
  `BuildingBlocks/Contracts/*` `StorefrontConfigChanged` — the projection Catalog publishes; extend to the
  full chart, and emit it at **go-live** and on **duplication**.
- `src/Services/Catalog` storefront go-live gate + duplication (StorefrontDuplicated) — where the chart is
  generated/cloned. `StorefrontDuplicatedConsumer` (Payments) already clones payment accounts.
- `src/Services/Payments/Infrastructure/SubscriptionService.cs` `ChargeRenewalAsync` — stamp the renewal
  `Payment.StorefrontId` (subs now carry `StorefrontId`, #210).
- `src/Services/Usage/Domain/Usage.cs` `UsageBalance` — add `StorefrontId`; thread through
  `UsageOverageCharge`/`UsageAutoLoadCharge` so Payments posts per storefront.
- Supplier/carrier accrual posters: `Ledger.CarrierCost`, `Ledger.CogsReversal`, `SupplierPayable`,
  `ReturnedGoodsValuedConsumer`, `ShippingLabelPurchasedConsumer`, `OrderCostsRecognizedConsumer`.
- `src/Admin` — a per-storefront chart-of-accounts view (Commerce ops / storefront detail).
- Tests to mirror/repoint: `LedgerAttributionTests` (esp. `A_sale_without_storefront_accounts_keeps_the_shared_revenue_and_tax_accounts` — this behaviour is being REMOVED), `ChargebackLedgerTests`, `OrderCostLedgerTests`.

## IMPLEMENTATION PLAN — phased (each its own PR, off fresh main)

### Phase 1 — Full per-storefront chart model + auto-provision + duplication clone (no fallback removal yet)
1. Extend `StorefrontLedgerAccounts` to the full chart (revenue, refunds-contra, tax, shipping, receivable,
   cash, processing-fees, chargeback-fees, COGS, supplier-payable, carrier-payable) — nullable during rollout.
2. Per-storefront code builders in `LedgerAccount.cs` (`…store-{id}…`, provider-scoped where needed).
3. Catalog generates the full chart at **go-live** (extend the go-live gate) and clones it on **duplication**;
   publish via an extended `StorefrontConfigChanged` (or a new `StorefrontLedgerChartChanged`).
4. `StorefrontLedgerConfigConsumer` projects the full chart. Migration for the new columns.
5. Tests: go-live provisions the chart; duplication clones it. No behaviour change to postings yet.

### Phase 2 — Route the SALES path fully per-storefront + remove its fallback
6. `PaymentEventProcessor` sale + chargeback use the full per-storefront chart (cash, fees included); require
   non-null accounts. `Ledger.Sale`/`Chargeback` drop the shared fallback (throw if unattributed).
7. Renewals: `ChargeRenewalAsync` stamps `Payment.StorefrontId` so the renewal sale posts per storefront
   (this absorbs `pay_renew_storefront_ledger`).
8. Repoint `LedgerAttributionTests`/`ChargebackLedgerTests` to the per-storefront accounts; delete the
   shared-fallback test.

### Phase 3 — Usage + supplier/carrier accruals per storefront + remove those fallbacks
9. `UsageBalance` gains `StorefrontId` (provisioned/threaded); `UsageOverageCharge`/`UsageAutoLoadCharge`
   carry it → Payments posts to the store's accounts. `Ledger` COGS/carrier/writeoff/supplier-payable drop
   the shared fallback.
10. Supplier/carrier payables fully per storefront (accrual + payout settlement per store).
11. Repoint `OrderCostLedgerTests` + related.

### Phase 4 — Remove the shared constants + guardrails + admin + docs
12. Delete the shared `Accounts.*` fallback constants (or keep only as poison values a test asserts are never
    posted). Add a guard/test: no journal line may reference a non-storefront account code.
13. Admin portal: per-storefront chart-of-accounts view; reflect provisioning state.
14. Wiki: rewrite the ledger-attribution sections (remove "falls back to shared"); update project-analysis.

## TESTING STRATEGY
Unit: chart generation/clone; each `Ledger.*` factory rejects unattributed postings; per-store balances.
Integration: go-live provisions the chart; a sale/refund/chargeback/renewal/usage-charge/COGS all post to the
store's own accounts; trial balance still nets to 0 per store; an unprovisioned storefront cannot post (guarded
by go-live provisioning). Cap Npgsql pool ([[3commerce-integration-test-conn-pools]]).

## ACCEPTANCE CRITERIA
- [ ] Every new journal line references a storefront-scoped account; no line posts to a shared/default account.
- [ ] A storefront's full chart is auto-provisioned at go-live and cloned on duplication.
- [ ] Sales, refunds, chargebacks, renewals, usage overage/auto-load, COGS, carrier/supplier accruals all post
      per storefront; per-store trial balances net to 0.
- [ ] Forward-only: historical entries untouched; a guard/test prevents new shared-account postings.
- [ ] Admin portal surfaces the per-storefront chart. Wiki updated. All validation commands pass.

## NOTES / risks
- **Biggest risk**: a live storefront without a provisioned chart would fail to post once fallbacks are gone —
  hence Phase 1 (provision at go-live) MUST land and backfill-provision existing live storefronts before any
  fallback is removed in Phase 2+. A one-off provisioning pass for already-live storefronts is required.
- Provider-scoped accounts (cash/fees) are per-(storefront, provider); the chart builder must enumerate the
  storefront's configured providers (its payment accounts, ADR-0043).
- Confidence per phase: P1 high; P2/P3 medium (touching the hot sale/chargeback path — proceed test-first).
