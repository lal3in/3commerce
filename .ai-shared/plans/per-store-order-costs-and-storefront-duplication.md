# Feature: Per-store order costs, chargebacks, and storefront duplication

The following plan should be complete, but it's important that you validate documentation and codebase patterns and task sanity before you start implementing.

Pay special attention to naming of existing utils, types and models. Import from the right files etc.

## Feature Description

Close the cost-side gaps identified in the "E-Commerce Order Fulfillment Cost Breakdown" analysis (2026-08-02), per storefront. Today the ledger books per-store INCOME (revenue.store-*, tax.store-*, shipping.store-*) and per-PSP processing fees, but almost nothing the platform PAYS around an order: carrier label cost is never posted, the COGS accrual domain code (`SupplierPayable.ToAccrualEntry`) has NO production caller, RMA storage dispositions never write anything off, and chargebacks don't exist as a concept. Separately, the storefront-strategy request: duplicate an existing storefront (same commerce config + catalog, different look) — enabled by wiring the already-built-but-unwired per-storefront theme tokens (mt5_6) and adding a duplicate endpoint. Finally, a per-store "cost assumptions" config feeds an estimated-margin view for costs that never move real money (packaging, labor, marketing, buffer).

## User Story

As a platform operator
I want every per-order cost (carrier labels, COGS, return write-offs, chargebacks) booked per storefront, plus the ability to clone a storefront with a different theme
So that Financials shows a true per-store contribution margin and I can run multiple visually-distinct stores off one proven configuration.

## Problem Statement

- Shipping looks like pure profit: `shipping.store-{id}` income posts, but the carrier cost of `BuyLabelAsync` vanishes.
- `expense.cogs` exists and is tested, but nothing in the live flow ever calls `SupplierPayable.Create`/`ToAccrualEntry` — COGS is zero in every P&L.
- RMA `Storage` disposition (Damage/Incomplete/UnfitForSale) is a Support-side record only; the lost goods value never hits the books, and a Restock never reverses the (future) COGS accrual, so restocked-and-resold units would double-expense.
- No chargeback/dispute concept anywhere (`PaymentWebhookKind` has only PaymentSucceeded/PaymentFailed).
- The Financials P&L `Fees` line sums ALL `expense.*` — the moment new expense accounts land it silently swallows them.
- Theme tokens (`ThemeTokens`, `mergeTheme`, `ThemeStyle`) are implemented and sanitized but `layout.tsx:25` passes `mergeTheme(null)` — no per-storefront look.
- No storefront duplication; recreating a store means re-doing tax config, product assignments, navigation by hand.

## Solution Statement

Four phases, each independently shippable as its own PR:

1. **Per-store cost postings** — carrier-label cost accrual (Fulfillment → event → Payments ledger), wire the dormant COGS accrual per order per supplier with per-store `expense.cogs.store-{id}` attribution, and RMA disposition postings (Restock = accrual reversal, Storage = reclass to `expense.writeoffs.store-{id}`). Split the Financials `Fees` line FIRST so nothing gets swallowed.
2. **Chargebacks** — new webhook kind → `Ledger.Chargeback` posting (sale reversal + `expense.{provider}_chargeback_fees`), `PaymentStatus.Disputed`, dev simulate support, Financials line.
3. **Storefront duplication + per-store theme** — persist sanitized theme tokens on `Storefront`, expose via the existing public config endpoint, wire `RootLayout`; `POST /admin/storefronts/{id}/duplicate` cloning commerce config + product publications + navigation + theme while auto-deriving FRESH ledger accounts (safe by design: `DefaultAccountCode` derives from the new id).
4. **Cost assumptions / estimated margin** — per-store bps rates (packaging, labor, marketing, buffer) stored as config (NO journal postings), rendered as an estimated-margin column in Financials.

## Feature Metadata

**Feature Type**: New Capability (4 sub-features)
**Estimated Complexity**: High (as a whole); each phase Medium
**Primary Systems Affected**: Payments (ledger), Fulfillment, Ordering, Support, Catalog, Admin (Blazor), Storefront (Next.js), Contracts
**Dependencies**: none new — MassTransit contracts, EF Core migrations, existing carrier seam

---

## CONTEXT REFERENCES

### Relevant Codebase Files IMPORTANT: YOU MUST READ THESE FILES BEFORE IMPLEMENTING!

Ledger core (the pattern for EVERY posting):
- `src/Services/Payments/Domain/Ledger/Ledger.cs` — `Sale`/`Refund` factories, `Debit`/`Credit` helpers, receivable-bridge pattern, per-store account fallbacks. New factories (`Chargeback`, carrier-cost, COGS-reversal) MIRROR this.
- `src/Services/Payments/Domain/Ledger/LedgerAccount.cs` — `Accounts` constants + `CashFor`/`FeesFor` derivation pattern; `LedgerProviders.Normalize`. Add new constants + `ChargebackFeesFor(provider)` here.
- `src/Services/Payments/Domain/Ledger/JournalEntry.cs` — balanced, append-only, currency denormalized per line.
- `src/Services/Payments/Api/ChartOfAccountsSeeder.cs` (line ~62) — every SHARED fallback account must be seeded here. Per-store `*.store-{id}` codes are NOT seeded (balances group by code regardless).
- `src/Services/Payments/Domain/Ledger/StorefrontLedgerAccounts.cs` + `src/Services/Payments/Infrastructure/Consumers/StorefrontLedgerConfigConsumer.cs` — how Catalog's per-store account codes are projected into Payments.
- `src/Services/Payments/Infrastructure/PaymentEventProcessor.cs` — webhook → ledger truth; idempotent by `WebhookInbox.EventId`. Chargeback handling goes in this switch.
- `src/Services/Payments/Infrastructure/Consumers/ExecuteRefundConsumer.cs` — idempotency-by-entity pattern (`db.Refunds.AnyAsync`), proportional tax/shipping split (banker's rounding), storefront account lookup. MIRROR for all new consumers.
- `src/Services/Payments/Domain/Payment.cs` — `PaymentStatus` enum lives here (add `Disputed`); `ShippingMinor`/`TaxMinor` fields.
- `src/Services/Payments/Domain/IPaymentProvider.cs` (line 54) — `PaymentWebhookKind` enum; `PaymentWebhookEvent` record (has `FeeMinor`, `FailureReason`).
- `src/Services/Payments/Domain/SupplierPayables.cs` (lines 202–275) — `SupplierPayablePolicy` (CommissionBps), `SupplierPayable.Create`, `ToAccrualEntry` (Dr `expense.cogs` / Cr `liability.supplier_payable`). **Currently dead code in production — no caller.** Phase 1 wires it.

Fulfillment (carrier cost source):
- `src/Services/Fulfillment/Infrastructure/ShipmentService.cs` (lines 33–48) — `BuyLabelAsync`: provider fallback to `FakeCarrierProvider`, `LabelRequest("standard", Placeholder, Placeholder, parcel)`, `package.ApplyLabel`. Cost capture goes here.
- `src/Services/Fulfillment/Domain/Carriers/CarrierProviders.cs` — `CarrierLabel(Carrier, TrackingNumber, LabelUrl)` record (extend with cost), `ICarrierLabelProvider`, `CarrierRate(..., AmountMinor, Currency, ...)`.
- `src/Services/Fulfillment/Infrastructure/Carriers/CarrierProviders.cs` (lines 10–38) — `FakeCarrierProvider`: rate formula `500 + weightUnits*150 (+1500 crossBorder)` AUD; `CreateLabelAsync` must return a deterministic cost using the same formula.
- `src/Services/Fulfillment/Api/Endpoints/AdminShipmentsEndpoints.cs` — `BuyLabel` endpoint (line ~51), `AssignTracking` shows the publish-event-then-save pattern (line ~90: `publisher.Publish(new TrackingAssigned(...))`).
- `src/Services/Fulfillment/Domain/Shipment.cs` — Shipment has `OrderId` (the storefront attribution key; Package does NOT carry it — join via ShipmentId).

Ordering (cost knowledge):
- `src/Services/Ordering/Domain/Order.cs` — READ to confirm line fields: order lines carry `SupplierId` (migration 20260624161133_SupplierIdOnLine) and per-unit supplier cost comes from `ProductCopy.SupplierCostMinor` (`src/Services/Ordering/Domain/ProductCopy.cs:13`). Verify whether the line snapshots cost directly or needs the copy join.
- `src/Services/Ordering/Api/Endpoints/CheckoutEndpoints.cs` — checkout → `AuthorizePayment(..., taxMinor, shippingMinor)` publish pattern (line ~151).
- `src/Services/Ordering/Domain/Pricing.cs` — `PricingLineInput.SupplierCostMinor` already flows through pricing.

Support (RMA):
- `src/Services/Support/Domain/RmaDisposition.cs` — `RmaDispositionKind` (Restock/Storage), `RmaStorageReason` (Damage/Incomplete/UnfitForSale).
- `src/Services/Support/Api/Endpoints/AdminRmaEndpoints.cs` — `SetDisposition` (line ~25) is the hook point; Restock already publishes `RestockRequested`.
- `src/Services/Support/Infrastructure/Sagas/RmaState.cs` — READ to learn what order/line/qty data the saga holds (needed to value the returned goods).

Catalog / Storefront (duplication + theme):
- `src/Services/Catalog/Domain/Storefront.cs` — full aggregate: `Create`, `ConfigureCommerce`, `SetLedgerAccounts` (null ⇒ auto-derive `{kind}.store-{id:N}` — this is WHY a clone gets fresh books for free), `SetDefaultLanguage`, `SetVisibility`, `AddDomain`, state machine. Theme storage + `DuplicateFrom` go here.
- `src/Services/Catalog/Domain/Product.cs` (lines ~147–215) — `StorefrontNavigationItem`, `ProductPublication.Assign(tenantId, storefrontId, product, now)` — the two collections a duplicate must copy.
- `src/Services/Catalog/Api/Endpoints/StorefrontEndpoints.cs` — route table (lines 23–52): admin CRUD, `AssignProduct`, publish/unpublish, and `publicGroup.MapGet("/public", GetPublicConfig)` — theme rides on this public config. `PublishConfigAsync` publishes `StorefrontConfigChanged`.
- `src/BuildingBlocks/Contracts/Catalog/StorefrontConfigChanged.cs` — contract; new fields MUST have back-compatible defaults (pattern: `string ShippingAccountCode = ""`).
- `src/Storefront/lib/theme.ts` — `ThemeTokens` (colorPrimary, colorBg, colorText, colorMuted, fontSans, radius), `safeTokenValue` sanitizer (max 100 chars, regex allow/deny), `mergeTheme`. The C# sanitizer must mirror `SAFE_VALUE`/`DANGEROUS` exactly.
- `src/Storefront/app/layout.tsx` (lines 19–29) — currently `mergeTheme(null)`; READ how the layout resolves locale to find the storefront-resolution mechanism (host/env) and fetch the public config the same way.

Admin Financials + i18n:
- `src/Admin/Components/Pages/Financials.razor` — P&L per currency, `Sum(currency, prefixMatch, creditNormal)` helper, `Fees(c)` currently `expense.*` (MUST be narrowed), by-storefront table with `AccountAmount(code, creditNormal)`, `StorefrontDto`.
- `src/Admin/Components/Pages/Ledger.razor` — `Classify` for the Amount column keys off revenue+tax movement; new entry kinds (chargeback, cogs, writeoff, carrier cost) need classification.
- `src/Admin/Resources/SharedResource*.resx` (×6: base, de, es, fr, yue, zh) — key-balanced (currently 1234 keys each).

Tests to mirror:
- `src/Services/Payments/tests/LedgerAttributionTests.cs` — per-store routing assertion style (assert credit to `shipping.store-x`, 0 to shared).
- `src/Services/Payments/tests/SupplierPayableTests.cs` — existing accrual assertions (`expense.cogs` debit 8_000 at line 67).
- `tests/3commerce.IntegrationTests/MoneyFlowTests.cs` — end-to-end checkout → ledger assertions.
- `tests/3commerce.IntegrationTests/LedgerInvariantTests.cs` — balance invariant (Σdebits = Σcredits) — every new factory must pass it.
- `tests/3commerce.IntegrationTests/ShipmentPackageTests.cs` — add-package → buy-label → tracking flow.
- `src/Services/Fulfillment/tests/PackageTests.cs`, `CarrierAdapterTests.cs`.

### New Files to Create

- `src/BuildingBlocks/Contracts/Fulfillment/ShippingLabelPurchased.cs` — `record ShippingLabelPurchased(Guid PackageId, Guid ShipmentId, Guid OrderId, Guid TenantId, string Carrier, long CostMinor, string Currency)`.
- `src/BuildingBlocks/Contracts/Ordering/OrderCostsRecognized.cs` — `record OrderCostsRecognized(Guid OrderId, Guid? StorefrontId, Guid TenantId, string Currency, IReadOnlyList<SupplierCostItem> Items)`; `record SupplierCostItem(Guid SupplierEntityId, long CostMinor)`.
- `src/BuildingBlocks/Contracts/Support/RmaDispositionSet.cs` — `record RmaDispositionSet(Guid RmaId, Guid OrderId, int Kind, int? StorageReason)` (enums numeric on the wire — AGENTS.md invariant).
- `src/BuildingBlocks/Contracts/Ordering/ReturnedGoodsValued.cs` — `record ReturnedGoodsValued(Guid RmaId, Guid OrderId, Guid? StorefrontId, long CostMinor, string Currency, int Kind, int? StorageReason)`.
- `src/Services/Payments/Infrastructure/Consumers/ShippingLabelPurchasedConsumer.cs`
- `src/Services/Payments/Infrastructure/Consumers/OrderCostsRecognizedConsumer.cs`
- `src/Services/Payments/Infrastructure/Consumers/ReturnedGoodsValuedConsumer.cs`
- `src/Services/Ordering/Infrastructure/Consumers/RmaDispositionSetConsumer.cs` (values returned goods from order lines, republishes `ReturnedGoodsValued`)
- `src/Services/Payments/tests/OrderCostLedgerTests.cs` — unit tests for the three new posting shapes + chargeback.
- Phase 3: theme value object + duplicate handler live inside existing `Storefront.cs`/`StorefrontEndpoints.cs`; Next.js fetch helper `src/Storefront/lib/storefront-config.ts` if none exists.
- EF migrations per affected service (Payments, Catalog) — one per phase per service.

### Relevant Documentation YOU SHOULD READ THESE BEFORE IMPLEMENTING!

Internal (authoritative — this repo):
- `docs/adr/` — ADR-0014 (single refund path, append-only ledger), ADR-0028 (Offer/supply), ADR-0039 (refund down the settling PSP's rail), ADR-0040 (per-store refund reversal). Skim `ls docs/adr` for the exact filenames; cite the matching ADR in new XML doc comments the way `Ledger.cs` does.
- `.ai-shared/plans/multi-tenant-platform-expansion-phase-4-shipping-inventory-fulfillment.md` and `phase-5-marketing-theme-seo.md` — the mt4_*/mt5_6 task ids referenced in code comments come from these; keep the id convention in new comments.
- `AGENTS.md` (repo root, if present) — "enums numeric on the wire" invariant cited in `MfaEndpoints.cs:126`.

External:
- MassTransit consumer docs — https://masstransit.io/documentation/concepts/consumers (idempotent consumer shape; but the in-repo consumers are the better template).
- EF Core migrations — https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/ (only if the in-repo gotchas below are insufficient).

### Patterns to Follow

**Ledger posting factory** (from `Ledger.cs`): static factory returning a balanced `JournalEntry`; account fallback via `string.IsNullOrWhiteSpace(x) ? Accounts.Shared : x`; guard clamps (`Math.Clamp`); XML doc explaining the accounting; `Debit(entry, account, minor)` / `Credit(...)` helpers only.

**Idempotent consumer** (from `ExecuteRefundConsumer.cs`): early-return if the entity/reference already exists; look up `StorefrontLedgerAccounts` via `payment.StorefrontId is { } sid ? await db.StorefrontLedgerAccounts... : null`; proportional splits use `(long)Math.Round((decimal)a * b / c, MidpointRounding.ToEven)`; single `SaveChangesAsync`; publish completion event after save.

**Per-store account derivation**: Catalog owns configured codes (`Storefront.SetLedgerAccounts`, auto-derive `{kind}.store-{id:N}`); Payments-side derived codes that need no operator override (cogs/writeoff/shipping-cost) are derived IN PAYMENTS as `$"expense.{kind}.store-{sid:N}"` from `payment.StorefrontId`/message StorefrontId — do NOT add more Catalog columns unless operators must override them.

**Contract evolution**: append-only, defaulted parameters (`long ShippingMinor = 0`, `string ShippingAccountCode = ""`).

**EF migration gotchas** (memory: 3commerce-ef-migration-format): run `dotnet ef migrations add <Name> --project src/Services/<S>/Infrastructure --startup-project src/Services/<S>/Api` WITHOUT `--no-build`, then ALWAYS `dotnet format src/Services/<S>/Infrastructure/*.csproj` before committing.

**resx i18n** (6 files, key-balanced): insert via Python (xml-aware or value-agnostic regex anchored on a FULL `</data>` close — NEVER anchor on a `<data>` open tag; `Fin.Revenue` is multi-line and broke a naive insert on 2026-08-02). Validate all 6 with `xml.etree.ElementTree.parse` + equal key counts.

**Blazor page pattern** (from `Financials.razor`): `@rendermode AdminRenderModes.InteractiveServerNoPrerender`, `GatewayClient` + `SafeList<T>` catch-to-empty, `L["..."]` for every string, records at the bottom of `@code`.

**Anti-patterns to avoid**: posting unbalanced entries (DB constraint throws at save); prefix-summing `expense.*` for a single P&L line (phase 1 splits it); editing journal entries (append-only — corrections are reversing entries); adding cost columns to `CarrierLabel` consumers without updating `FakeCarrierProvider` AND the label-provider interface implementations.

---

## IMPLEMENTATION PLAN

### Phase 1: Per-store cost postings (carrier cost, COGS wiring, RMA dispositions)

Foundation first: split the Financials `Fees` line and add the new account constants + seeder rows, THEN add postings. Ship as one PR (`feat/per-store-order-costs`).

### Phase 2: Chargebacks

Webhook kind → ledger reversal + fee → payment status → simulate endpoint → Financials/Ledger UI. PR `feat/chargebacks`.

### Phase 3: Storefront duplication + per-store theme

Theme persistence + public exposure + Next.js wiring; then the duplicate endpoint + Admin button. PR `feat/storefront-duplicate-theme`.

### Phase 4: Cost assumptions / estimated margin

Config JSON on storefront + admin editor + Financials estimated-margin column. No ledger writes. PR `feat/store-cost-assumptions`.

---

## STEP-BY-STEP TASKS

### Phase 1 — per-store order costs

#### task_1 UPDATE `src/Services/Payments/Domain/Ledger/LedgerAccount.cs`
- **IMPLEMENT**: add constants `ExpenseShippingCarrier = "expense.shipping_carrier"`, `ExpenseWriteoffs = "expense.writeoffs"`, `LiabilityCarrierPayable = "liability.carrier_payable"`; add helpers `CogsStoreFor(Guid sid) => $"expense.cogs.store-{sid:N}"`, `ShippingCostStoreFor(Guid sid)`, `WriteoffsStoreFor(Guid sid)` (mirror `CashFor` style, doc comments citing the shared fallback).
- **PATTERN**: `LedgerAccount.cs:39-42`.
- **VALIDATE**: `dotnet build src/Services/Payments/Domain/3commerce.Payments.Domain.csproj --nologo -v q`

#### task_2 UPDATE `src/Services/Payments/Api/ChartOfAccountsSeeder.cs`
- **IMPLEMENT**: seed `expense.shipping_carrier` (Expense), `expense.writeoffs` (Expense), `liability.carrier_payable` (Liability).
- **PATTERN**: existing rows near line 62.
- **VALIDATE**: `dotnet build src/Services/Payments/Api/*.csproj --nologo -v q` (glob → the single Api csproj)

#### task_3 UPDATE `src/Admin/Components/Pages/Financials.razor` — split expense reporting BEFORE new postings exist
- **IMPLEMENT**: narrow `Fees(c)` to `expense.` codes ending in `_fees` (processing fees only); add `Cogs(c)` summing `expense.cogs*`, `ShippingCost(c)` summing `expense.shipping_carrier` + `expense.shipping.store-`, `Writeoffs(c)` summing `expense.writeoffs*` — all debit-normal. P&L gains lines `Fin.Cogs`, `Fin.ShippingCost`, `Fin.Writeoffs`; `NetResult = Revenue + ShippingIncome − Refunds − Fees − Cogs − ShippingCost − Writeoffs`. By-storefront table: add COGS + Shipping-cost columns via the Payments-derived codes (`expense.cogs.store-{s.Id:N}` — compute the code in the razor from `s.Id`, matching task_1 helpers) and a computed Margin column. Balance-sheet Liabilities: add `Fin.CarrierPayable` + `Fin.SupplierPayable` lines (`liability.carrier_payable`, `liability.supplier_payable` — supplier payable becomes visible once task_8 wires accruals).
- **GOTCHA**: `Sum(...)` account-code match is `StartsWith` — `expense.shipping.store-` vs `expense.shipping_carrier` don't collide but write tests for the prefix logic anyway. Column count changes → update `<thead>`.
- **VALIDATE**: `dotnet build src/Admin/3commerce.Admin.csproj --nologo -v q`

#### task_4 UPDATE Admin resx ×6 — new keys
- **IMPLEMENT**: `Fin.Cogs` ("Cost of goods sold"), `Fin.ShippingCost` ("Carrier shipping cost"), `Fin.Writeoffs` ("Inventory write-offs"), `Fin.CarrierPayable` ("Owed to carriers"), `Fin.SupplierPayable` ("Owed to suppliers"), `Fin.Col.Margin` ("Margin") in all 6 resx with real translations (de/es/fr/yue/zh).
- **GOTCHA**: anchor insertions after a complete `</data>` element; validate XML + key balance for all 6 afterwards.
- **VALIDATE**: `python3 -c "import xml.etree.ElementTree as ET,glob; files=sorted(glob.glob('src/Admin/Resources/SharedResource*.resx')); counts={f:len(ET.parse(f).getroot().findall('data')) for f in files}; print(counts); assert len(set(counts.values()))==1"`

#### task_5 UPDATE `src/Services/Fulfillment/Domain/Carriers/CarrierProviders.cs` + `src/Services/Fulfillment/Infrastructure/Carriers/CarrierProviders.cs`
- **IMPLEMENT**: extend `CarrierLabel` to `(CarrierCode Carrier, string TrackingNumber, string LabelUrl, long CostMinor = 0, string Currency = "AUD")`; `FakeCarrierProvider.CreateLabelAsync` computes a deterministic cost with its own rate formula (weightUnits from the request parcel: `500 + Math.Max(1, WeightGrams/500)*150`, "standard" service, AUD).
- **GOTCHA**: `LabelRequest` origin/destination are `Placeholder` (empty addresses) in `ShipmentService.BuyLabelAsync` — the fake cost formula must not depend on countries (skip the cross-border surcharge).
- **VALIDATE**: `dotnet test src/Services/Fulfillment/tests --nologo -v q --filter "PackageTests|CarrierAdapterTests"`

#### task_6 CREATE `src/BuildingBlocks/Contracts/Fulfillment/ShippingLabelPurchased.cs`; UPDATE `ShipmentService.BuyLabelAsync` + its registration
- **IMPLEMENT**: contract record as specified in New Files. `BuyLabelAsync` gains an `IPublishEndpoint` dependency (inject into `ShipmentService`); after `SaveChangesAsync`, load the package's shipment (`db.Shipments` by `package.ShipmentId`) for `OrderId` and publish `ShippingLabelPurchased(package.Id, package.ShipmentId, shipment.OrderId, package.TenantId, label.Carrier.ToString(), label.CostMinor, label.Currency)`.
- **PATTERN**: publish-then-save ordering as in `AdminShipmentsEndpoints.AssignTracking` (`AdminShipmentsEndpoints.cs:90-111`); DI registration of `ShipmentService` is in Fulfillment `Program.cs` — check its lifetime before adding the dependency.
- **GOTCHA**: re-buying a label for the same package would publish twice — the Payments consumer (task_7) must be idempotent by PackageId; also skip publish when `label.CostMinor == 0`.
- **VALIDATE**: `dotnet test tests/3commerce.IntegrationTests --nologo -v q --filter ShipmentPackageTests`

#### task_7 CREATE `src/Services/Payments/Infrastructure/Consumers/ShippingLabelPurchasedConsumer.cs` + `Ledger.CarrierCost` factory + register consumer
- **IMPLEMENT**: `Ledger.CarrierCost(Guid packageId, Guid orderId, long costMinor, string currency, DateTimeOffset now, string? storeExpenseAccount)` → Dr `storeExpenseAccount ?? Accounts.ExpenseShippingCarrier` / Cr `Accounts.LiabilityCarrierPayable`, description `$"Carrier label for order {orderId}"`, reference = packageId. Consumer: idempotent via `db.JournalEntries.AnyAsync(e => e.Reference == msg.PackageId.ToString())`; storefront lookup via `db.Payments.SingleOrDefault(p => p.OrderId == msg.OrderId)` → `Accounts.ShippingCostStoreFor(sid)` when `StorefrontId` present. Register the consumer wherever the existing Payments consumers are registered (find `AddConsumer<StorefrontLedgerConfigConsumer>` in Payments `Program.cs` and mirror).
- **PATTERN**: `ExecuteRefundConsumer.cs` (idempotency + accounts lookup); `Ledger.cs` factory shape.
- **VALIDATE**: `dotnet test src/Services/Payments/tests --nologo -v q`

#### task_8 CREATE `src/BuildingBlocks/Contracts/Ordering/OrderCostsRecognized.cs`; UPDATE Ordering to publish it; CREATE `OrderCostsRecognizedConsumer` in Payments — WIRES THE DORMANT COGS
- **IMPLEMENT**: (a) READ `src/Services/Ordering/Domain/Order.cs` to confirm where the paid transition happens and what cost fields lines carry (SupplierId per line; unit cost from ProductCopy.SupplierCostMinor — snapshot at order creation if not already on the line). (b) In the consumer/handler where the order becomes paid (the `PaymentSucceeded` consumer in Ordering — find it via `grep -rn "PaymentSucceeded" src/Services/Ordering`), aggregate `line.SupplierCostMinor * qty` per SupplierId and publish `OrderCostsRecognized`. (c) Payments consumer: idempotent by `db.SupplierPayables.AnyAsync(p => p.OrderId == msg.OrderId)`; per item: look up active `SupplierPayablePolicy` for (TenantId, SupplierEntityId) else treat CommissionBps = 0 (construct an inline default policy); `SupplierPayable.Create(...)` + accrual entry. (d) EXTEND `SupplierPayable.ToAccrualEntry(DateTimeOffset now, string? cogsAccount = null)` to debit `cogsAccount ?? Accounts.CostOfGoodsSold`; pass `Accounts.CogsStoreFor(sid)` when the message has a StorefrontId.
- **GOTCHA**: `SupplierPayableTests.cs:67` asserts the shared `expense.cogs` — keep the default-parameter path green and ADD per-store assertions. Zero-cost items (SupplierCostMinor == 0) publish nothing.
- **VALIDATE**: `dotnet test src/Services/Payments/tests --nologo -v q && dotnet test src/Services/Ordering/tests --nologo -v q`

#### task_9 CREATE `RmaDispositionSet` + `ReturnedGoodsValued` contracts; UPDATE Support `SetDisposition`; CREATE Ordering `RmaDispositionSetConsumer`; CREATE Payments `ReturnedGoodsValuedConsumer` + `Ledger.CogsReversal`/`Ledger.Writeoff`
- **IMPLEMENT**: (a) READ `src/Services/Support/Infrastructure/Sagas/RmaState.cs` + `AdminRmaEndpoints.cs` to learn what item/qty data an RMA holds; enrich `RmaDispositionSet` with whatever identifies the returned lines (at minimum OrderId + per-line ProductId/Qty if available, else OrderId + RefundedMinor share). (b) Support publishes it from `SetDisposition` on create AND edit (an edit from Restock→Storage must post the delta — simplest correct rule: post reversing entries referencing `{RmaId}:{revision}` and document it). (c) Ordering consumer values the returned goods from the order's lines (SupplierCostMinor × returned qty; proportional when only an amount share is known) and publishes `ReturnedGoodsValued`. (d) Payments consumer, idempotent by reference: Kind==Restock → Dr `liability.supplier_payable` / Cr `expense.cogs.store-{sid}` (accrual reversal — restocked goods will re-accrue COGS when resold, so without this they double-expense); Kind==Storage → Dr `expense.writeoffs.store-{sid}` (fallback `expense.writeoffs`) / Cr `expense.cogs.store-{sid}` (reclass: total expense unchanged, write-off visible as its own P&L line).
- **GOTCHA**: only post when a COGS accrual exists for the order (guard `db.SupplierPayables.AnyAsync(OrderId)`), otherwise the credit side dangles. Skip Kind==Restock posting if the RMA never had an accrual.
- **VALIDATE**: `dotnet test src/Services/Payments/tests --nologo -v q` (new `OrderCostLedgerTests` cover both shapes) and Support/Ordering test projects.

#### task_10 UPDATE `src/Admin/Components/Pages/Ledger.razor` `Classify`
- **IMPLEMENT**: classify the new entry kinds for the Amount column: carrier-cost + writeoff + COGS accrual entries are outgoing (red); COGS reversal incoming (green). Key off account prefixes on the lines (e.g. any `expense.` debit with no `revenue.` movement ⇒ outgoing), not description text.
- **VALIDATE**: `dotnet build src/Admin/3commerce.Admin.csproj --nologo -v q`

#### task_11 ADD tests — `src/Services/Payments/tests/OrderCostLedgerTests.cs` + extend `tests/3commerce.IntegrationTests/MoneyFlowTests.cs`
- **IMPLEMENT**: unit: carrier cost posts to `expense.shipping.store-{id}` when attributed and `expense.shipping_carrier` when not; COGS accrual per store; restock reversal balances; storage reclass balances; all entries pass Σdebit==Σcredit. Integration: after a taxed checkout + label purchase, balances show the carrier-cost debit and `liability.carrier_payable` credit; COGS accrual equals Σ(line SupplierCostMinor×qty net of commission).
- **PATTERN**: `LedgerAttributionTests.cs` naming style (`A_sale_books_...`).
- **VALIDATE**: `dotnet test src/Services/Payments/tests --nologo -v q && dotnet test tests/3commerce.IntegrationTests --nologo -v q --filter "MoneyFlow|LedgerInvariant"`

#### task_12 Phase-1 finalization
- **IMPLEMENT**: full build; `dotnet format 3commerce.sln --no-restore`; full-stack restart (`scripts/run-all.sh stop && scripts/run-all.sh start` — memory: prefer full-stack restart) + fresh reseed if needed; Playwright-verify Financials shows COGS/carrier-cost/write-off lines and per-store margin; commit (NO AI trailers), PR, merge-on-green poller, sync main.
- **VALIDATE**: `dotnet build 3commerce.sln --nologo -v q && dotnet format 3commerce.sln --no-restore --verify-no-changes`

### Phase 2 — chargebacks

#### task_13 UPDATE `src/Services/Payments/Domain/IPaymentProvider.cs` + `Payment.cs`
- **IMPLEMENT**: `PaymentWebhookKind.ChargebackOpened = 3`; `PaymentStatus.Disputed` (append, don't renumber); `Accounts` helper `ChargebackFeesFor(provider) => $"expense.{LedgerProviders.Normalize(provider)}_chargeback_fees"`; seed the 5 known-provider chargeback-fee accounts in `ChartOfAccountsSeeder`.
- **VALIDATE**: `dotnet build src/Services/Payments/Api/*.csproj --nologo -v q`

#### task_14 ADD `Ledger.Chargeback` + `PaymentEventProcessor` case
- **IMPLEMENT**: factory mirrors `Refund` (per-store revenue/tax/shipping reversal through the receivable bridge, gross out of `cash.{provider}`) PLUS Dr `ChargebackFeesFor(provider)` / Cr cash for `ev.FeeMinor`; description `$"Chargeback for order {orderId} via {methodKind}"`. Processor case `ChargebackOpened when payment.Status == PaymentStatus.Succeeded`: post entry (full remaining amount − already-refunded), set `Status = Disputed`, publish a new `PaymentDisputed(OrderId, PaymentIntentId, AmountMinor)` contract so Ordering can flag the order.
- **GOTCHA**: guard against chargeback-after-full-refund (remaining == 0 → log + inbox-only, no entry). Proportional tax/shipping via the `ExecuteRefundConsumer` rounding pattern.
- **VALIDATE**: `dotnet test src/Services/Payments/tests --nologo -v q`

#### task_15 UPDATE dev simulate endpoint + Ordering + Admin UI
- **IMPLEMENT**: find the dev simulate webhook endpoint (`grep -rn "simulate" src/Services/Payments/Api`) and accept kind 3 (+ optional fee); Ordering consumes `PaymentDisputed` → order flag/status surfaced on Orders pages (badge like PartiallyRefunded from #145); Financials: chargeback fees flow into `Fees` automatically only if the account name ends `_fees` — confirm the narrowed matcher includes `_chargeback_fees`, and add a distinct `Fin.Chargebacks` P&L line summing them separately if it does. Ledger.razor Classify: chargeback = outgoing (red). i18n keys ×6 resx + storefront JSON ×6 if the storefront shows a disputed badge (optional — keep storefront out of scope if not).
- **VALIDATE**: `dotnet build 3commerce.sln --nologo -v q`; simulate a chargeback against a seeded order via curl and assert the balanced entry via `GET /api/payments/admin/ledger/balances`.

#### task_16 Phase-2 tests + finalization
- **IMPLEMENT**: unit tests (chargeback posting balanced, per-store reversal, fee account, refund-then-chargeback guard); integration MoneyFlowTests chargeback case; format/build/live-verify/commit/PR/merge/sync as task_12.
- **VALIDATE**: `dotnet test src/Services/Payments/tests --nologo -v q && dotnet test tests/3commerce.IntegrationTests --nologo -v q --filter MoneyFlow`

### Phase 3 — storefront duplication + per-store theme

#### task_17 UPDATE `src/Services/Catalog/Domain/Storefront.cs` — theme storage
- **IMPLEMENT**: `public string ThemeJson { get; private set; } = "";` + `SetTheme(string? themeJson, DateTimeOffset now)` validating: parseable JSON object, only the six known token keys (colorPrimary, colorBg, colorText, colorMuted, fontSans, radius), each value ≤100 chars and matching the sanitizer (port `SAFE_VALUE = ^[#a-zA-Z0-9.,()%\s_-]+$` and `DANGEROUS = url\(|expression|javascript:|@import|[;{}<>]` from `src/Storefront/lib/theme.ts:23-24` verbatim); null/blank leaves current value (the SetDefaultLanguage convention). Catalog migration `StorefrontTheme` + `dotnet format` the Infrastructure csproj.
- **VALIDATE**: `dotnet test src/Services/Catalog/tests --nologo -v q`

#### task_18 UPDATE `StorefrontEndpoints.cs` — theme on create/update/public-config; UPDATE Next.js layout
- **IMPLEMENT**: `ThemeJson`/theme dict on Create/Update requests + `StorefrontResponse` + `GetPublicConfig` response. Next.js: READ how `layout.tsx` resolves locale/storefront (find the existing public-config fetch under `src/Storefront/lib/` or `app/api/`); fetch the public config server-side and pass `mergeTheme(config.theme)` to `ThemeStyle` — client sanitizer stays as defense-in-depth. Admin: theme editor fields (6 inputs + preview swatch) on the storefront settings page; i18n ×6.
- **GOTCHA**: `GetPublicConfig` is anonymous/cached — check for output caching before assuming instant theme updates; document the staleness if cached.
- **VALIDATE**: `cd src/Storefront && npm run lint && npm run build`; Playwright: set a theme via admin API, storefront `:root` shows the CSS vars.

#### task_19 ADD `POST /admin/storefronts/{id}/duplicate`
- **IMPLEMENT**: Catalog endpoint: load source with Domains + its ProductPublications + StorefrontNavigationItems; `Storefront.Create(tenantId, request.Name, now)`; copy via `ConfigureCommerce(publicUrl: "", source.Currency, source.TaxRegime, source.TaxRateBasisPoints, now)`, `SetDefaultLanguage(source.DefaultLanguage, now)`, `SetTheme(source.ThemeJson, now)`, `SetLedgerAccounts(null, null, null, now, null)` (auto-derives FRESH `*.store-{newId:N}` codes — the invariant that makes cloning safe); re-`ProductPublication.Assign` each published product; copy navigation items. Deliberately NOT copied: domains, PublicUrl, visibility (default Private), state (Draft), AccessPasswordHash, account codes. Call `PublishConfigAsync` for the new store. Audit-record via `IAuditRecorder` (pattern: `SupplierPayoutAdminEndpoints.CreateBankAccount`). Admin Storefronts page: Duplicate button + name prompt; i18n ×6.
- **GOTCHA**: publications may reference unpublishable products — copy assignments as-is (readiness is re-checked at publish/activate). New store MUST NOT be activatable until a domain is added (existing `CheckReadiness` already enforces).
- **VALIDATE**: `dotnet test src/Services/Catalog/tests --nologo -v q`; integration: duplicate a seeded store via curl → new store lists the same product count, `receivable/revenue/tax/shipping` codes all contain the NEW id, zero domains, state Draft.

#### task_20 Phase-3 tests + finalization
- **IMPLEMENT**: Catalog unit tests (theme sanitizer rejects `url(...)`/`{}`; duplicate derives fresh codes, copies config, skips domains); Playwright: duplicate the EU store, restyle it, both render with different `:root` vars; ship as task_12.
- **VALIDATE**: full solution build + format + tests as task_12.

### Phase 4 — cost assumptions / estimated margin

#### task_21 UPDATE `Storefront.cs` + endpoints — `CostAssumptionsJson`
- **IMPLEMENT**: same shape as theme: private-set string, `SetCostAssumptions(string?, now)` validating a JSON object of known keys `{packagingBps, laborBps, marketingBps, insuranceBps, bufferBps}` each int 0–10000. Migration + projection to the admin API responses (NOT to StorefrontConfigChanged — Payments doesn't need it; Financials reads it from Catalog).
- **VALIDATE**: `dotnet test src/Services/Catalog/tests --nologo -v q`

#### task_22 UPDATE `Financials.razor` + storefront settings editor
- **IMPLEMENT**: by-storefront table gains "Estimated overheads" (Σ bps × StoreGross / 10000) and "Estimated net margin" (actual margin − estimated overheads) columns, visually distinct (italic/amber, tooltip `Fin.Estimated.Tip` explaining these are assumptions, not postings); admin storefront settings gets the 5 bps inputs; i18n ×6.
- **VALIDATE**: build + resx balance script (task_4 command).

#### task_23 Phase-4 finalization
- **IMPLEMENT**: tests (validation bounds; Financials math), live Playwright verify, ship as task_12.
- **VALIDATE**: `dotnet build 3commerce.sln --nologo -v q && dotnet format 3commerce.sln --no-restore --verify-no-changes`

---

## TESTING STRATEGY

### Unit Tests
xUnit per service (`src/Services/<S>/tests`). Ledger factories: every new posting shape asserts Σdebits == Σcredits, exact account codes (per-store AND shared-fallback variants), and clamping/guards — mirror `LedgerAttributionTests` naming (`A_sale_books_...`). Domain validation: theme/cost-assumption sanitizers (accept/reject tables), duplicate-storefront invariants.

### Integration Tests
`tests/3commerce.IntegrationTests`: extend `MoneyFlowTests` (checkout → label → chargeback → balances), keep `LedgerInvariantTests` green (it enforces the balance constraint DB-side), extend `ShipmentPackageTests` for the publish-on-label-buy.

### Edge Cases
- Label re-bought for the same package (idempotent by PackageId reference).
- Chargeback after partial refund (only the remaining amount reverses); after full refund (no entry).
- RMA disposition edited Restock↔Storage (revisioned reference, reversing entries).
- Order with zero supplier cost lines (no COGS event).
- Storefront-unattributed order (all fallbacks: `expense.shipping_carrier`, `expense.cogs`, `expense.writeoffs`, shared chargeback fees).
- Duplicate of a storefront that was never configured (blank PublicUrl, TaxRegime None) — must still clone cleanly.
- Theme JSON with hostile values (`url(javascript:...)`, `};body{display:none`) — rejected server-side.

## VALIDATION COMMANDS

### Level 1: Syntax & Style
```
dotnet build 3commerce.sln --nologo -v q
dotnet format 3commerce.sln --no-restore --verify-no-changes
cd src/Storefront && npm run lint && npm run build   # phase 3 only
```

### Level 2: Unit Tests
```
dotnet test src/Services/Payments/tests --nologo -v q
dotnet test src/Services/Fulfillment/tests --nologo -v q
dotnet test src/Services/Catalog/tests --nologo -v q
dotnet test src/Services/Ordering/tests --nologo -v q
dotnet test src/Services/Support/tests --nologo -v q
```

### Level 3: Integration Tests
```
dotnet test tests/3commerce.IntegrationTests --nologo -v q --filter "MoneyFlow|LedgerInvariant|ShipmentPackage"
```

### Level 4: Manual Validation
Full-stack restart (never single services — saved memory): `scripts/run-all.sh stop && scripts/run-all.sh start`; reseed when ledger history must reflect new logic: `--fresh --data full`. Then Playwright (import from `/Users/lehn/Documents/Git-Roots/3commerce/src/Storefront/node_modules/playwright/index.js`): admin Financials at :5200/financials (COGS/carrier/write-off/chargeback lines, per-store margin), Ledger page classification colours, storefront theme vars at :3000, duplicate-store flow. Ledger truth via `GET :8080/api/payments/admin/ledger/balances`.

### Level 5: Additional Validation
`codegraph explore "<symbol>"` before editing any listed file to re-check current line numbers — this plan's references decay as PRs merge. CI required checks: build-test, integration, changes (merge-on-green poller: scratchpad `poll_req.sh <PR>`).

## ACCEPTANCE CRITERIA

- [ ] Buying a label posts Dr `expense.shipping.store-{id}` (or `expense.shipping_carrier`) / Cr `liability.carrier_payable`, exactly once per package.
- [ ] A paid order accrues COGS per supplier to `expense.cogs.store-{id}` / `liability.supplier_payable` (the dormant `SupplierPayable` path is live).
- [ ] RMA Restock reverses the order's COGS accrual; Storage reclasses it to `expense.writeoffs.store-{id}`; both survive disposition edits.
- [ ] A simulated chargeback reverses the sale per-store + books the provider chargeback fee; payment shows Disputed.
- [ ] Financials: Fees no longer swallows non-fee expenses; new P&L lines + per-store margin columns render in all 6 languages (resx balanced).
- [ ] A duplicated storefront: same currency/tax/language/theme/product publications/navigation; FRESH ledger account codes containing the new id; no domains; Draft; Private.
- [ ] Storefront renders per-store theme CSS vars; hostile theme values rejected server-side.
- [ ] Estimated-margin columns compute from bps assumptions with zero journal entries written.
- [ ] All validation commands pass; no regressions (`LedgerInvariantTests`, existing 26 `LedgerAttributionTests`, `SupplierPayableTests`).
- [ ] Every PR: no AI-authorship trailers; merge-on-green via required checks.

## COMPLETION CHECKLIST

- [ ] Tasks executed in order, one PR per phase, main synced after each merge
- [ ] Each task's VALIDATE command passed immediately after the task
- [ ] EF migrations formatted (`dotnet format` on each Infrastructure csproj touched)
- [ ] resx ×6 XML-valid and key-balanced after every i18n task
- [ ] Full suite green incl. non-required browser-e2e (memory: all tests must pass, no gaps)
- [ ] Live Playwright verification screenshots taken per phase
- [ ] Plan status file updated as tasks complete

## NOTES

**Design decisions & rationale**
- **Accrual over cash for carrier/supplier costs**: no real money moves in dev; `liability.carrier_payable` mirrors the existing `liability.supplier_payable` shape and keeps `cash.*` truthful to PSP settlements only.
- **Payments-side derivation of expense codes** (`expense.cogs.store-{id:N}`): operators never override these, so no new Catalog columns/projection — deviates deliberately from the income-side pattern (where codes are operator-editable via `SetLedgerAccounts`).
- **Restock = COGS reversal, Storage = reclass**: COGS is expensed at sale; a restocked unit re-accrues on resale (double-expense without reversal); storage keeps the total expense but surfaces it as a write-off line. Net P&L stays consistent either way.
- **Event chain for RMA valuation** (Support → Ordering → Payments): Support doesn't know costs, Payments doesn't know lines — Ordering owns cost knowledge, so it enriches. Three small contracts beat one service reaching into another's schema.
- **Cost assumptions are config, not postings**: fake journal entries would corrupt the append-only ledger's meaning; estimates render visually distinct in the UI.
- **Duplication safety hinges on `SetLedgerAccounts(null,...)`** auto-derivation from the NEW id — never copy account-code strings between stores.
- Line-number references were verified 2026-08-02 against `main` @ 8179481 + `feat/shipping-per-store` (PR in flight when this plan was written — its per-store `shipping.store-*`/`ShippingAccountCode` changes are assumed merged).
- Out of scope (deliberately, dev-only platform): customs/duties, certifications, eco-fees, FX/consolidation, real carrier/PSP credentials, marketing attribution.

**Confidence score**: 7.5/10 for one-pass success — high for phases 2–4 (patterns fully mapped), lower for phase 1's RMA leg because `RmaState`'s line-level data is unverified (task_9 starts with the read for exactly this reason).
