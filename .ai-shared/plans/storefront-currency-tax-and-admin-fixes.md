# Feature: Storefront per-currency pricing + storefront tax + admin interaction fixes

The following plan should be complete, but it's important that you validate documentation and codebase patterns and task sanity before you start implementing. Pay special attention to naming of existing utils, types, and models. Import from the right files.

## Feature Description

Three related gaps surfaced during live review of the multi-storefront platform:

1. **Currency** — a storefront configured with a currency (e.g. AU store = AUD) still shows catalog products in EUR. Products are priced once (single `Variant.Currency`, defaulted to EUR by the importer) and the storefront never reads the storefront's currency.
2. **Tax** — a storefront's configured `TaxRegime`/`TaxRateBasisPoints` (e.g. AU GST 10%) is never applied; the storefront shows 0 / no tax.
3. **Admin buttons** — many admin actions "appear not to work": partly a Blazor pre-circuit timing race, partly action buttons that hit API 4xx/5xx and show no feedback, plus two genuine 500s (payment-account create enum-as-string; entity supplier lifecycle empty-body).

The chosen currency model (confirmed by product owner): **the tenant sets an explicit price per currency, per product** — NOT FX conversion, NOT a pricing-engine abstraction. If a storefront's currency has no tenant-set price for a product, **the product is hidden on that storefront**.

## User Story

As a **platform tenant operator**
I want to **set an explicit price per currency for each product, have each storefront display its own currency + legally-correct tax, and have every admin action give clear feedback**
So that **each regional storefront sells at tenant-controlled local prices with correct tax, and I can trust the admin UI.**

## Problem Statement

The Catalog `Storefront` record carries `Currency`/`TaxRegime`/`TaxRateBasisPoints` (added in the storefront_tax_2026_07_01 work), but neither the catalog pricing model nor the Next.js storefront consumes them: variants hold a single currency/price, the public catalog API returns that single price, and the storefront renders it verbatim with no tax. Separately, the Blazor admin's prerender interactivity gap + silent error handling + two contract-mismatch 500s make admin actions feel broken.

## Solution Statement

- **Catalog**: add tenant-authored per-currency prices to variants (`VariantPrice` child rows), expose them through `ProductUpserted`, the public product/search API (priced for a requested currency), and the admin product editor.
- **Storefront**: resolve the active storefront's config (new public read endpoint), display the price for the storefront currency (hide products with no price in that currency), and compute + show tax from the storefront's regime/rate; make the server-charged tax match.
- **Admin**: surface API errors on every action; fix the two 500s; fix the pre-circuit UX; minor cleanups (Ledger render mode, middleware order, favicon).

## Feature Metadata

**Feature Type**: Enhancement + Bug Fix
**Estimated Complexity**: High (per-currency pricing ripples through Catalog → contracts → Ordering → storefront → checkout)
**Primary Systems Affected**: Catalog (domain/API/migration), BuildingBlocks.Contracts, Ordering (projection/checkout/tax), Storefront (Next.js), Admin (Blazor), Payments/Entity (endpoint contracts), dev-data seed
**Dependencies**: None new. Uses existing EF Core 10 / Npgsql, MassTransit, Next.js, Blazor Server.

---

## CONTEXT REFERENCES

### Relevant Codebase Files — READ THESE BEFORE IMPLEMENTING

**Currency / pricing**
- `src/Services/Catalog/Domain/Product.cs` (Variant class, lines 104-126) — Why: `PriceMinor`+`Currency` single-currency model to extend with `VariantPrice`.
- `src/BuildingBlocks/Contracts/Catalog/ProductUpserted.cs` (lines 7-20) — Why: event carrying `MinPriceMinor`/`Currency` + `ProductVariantUpserted(VariantId,Sku,PriceMinor,Currency,...)`; must carry per-currency prices.
- `src/Services/Catalog/Api/Endpoints/ProductsEndpoints.cs` — Why: public `Search` (line 22, via `ISearchProvider`), `GetBySlug` (line 40), `VariantResponse` (line 112); must return price for a requested currency.
- `src/Services/Catalog/Domain/ISearchProvider.cs` (line 24, `MinPriceMinor`) — Why: search result shape; per-currency `MinPriceMinor`.
- `src/Services/Catalog/Api/Endpoints/AdminEndpoints.cs` (lines 130-255) — Why: product create/update + `PublishUpsertedAsync` (defaultCurrency fallback at 246-254); where per-currency prices are written + published.
- `src/Admin/Components/Pages/Catalog.razor` — Why: admin product/variant editor to add per-currency price entry (reuse `CurrencySelect`).
- `src/Services/Ordering/Domain/ProductCopy.cs` (ProductCopy lines 7-18, VariantCopy 25-29) — Why: Ordering projection of catalog prices; needs per-currency.
- `src/Services/Ordering/Api/Endpoints/CheckoutEndpoints.cs` (price revalidation line 54, `currency = cart.Items[0].Currency` line 62) — Why: checkout prices/validates against a currency.
- `src/Storefront/components/catalog/ProductCard.tsx` (line 28, `formatMoney(product.minPriceMinor, product.currency)`) — Why: price render point.
- `src/Storefront/lib/gateway.ts` (`ProductHit` line 6, `searchProducts` line 54, `getProduct`, `AddressPurpose` normalize pattern lines 117-128) — Why: add storefront-currency param + `getStorefrontConfig`.
- `src/Storefront/lib/money.ts` (`formatMoney`) — Why: Intl currency formatter (already currency-arg driven).
- `src/Storefront/app/[storefront]/page.tsx` — Why: hardcoded `LOCAL_STOREFRONT_LABELS`; replace with real config.

**Tax**
- `src/Services/Catalog/Domain/Storefront.cs` (`Currency`, `TaxRegime`, `TaxRateBasisPoints`, `StorefrontTaxRegime` enum) — Why: source of truth for storefront currency/tax.
- `src/Services/Payments/Domain/ITaxStrategy.cs`, `src/Services/Payments/Infrastructure/FlatRateTaxStrategy.cs`, `src/Services/Payments/Infrastructure/Consumers/AuthorizePaymentConsumer.cs` — Why: server-side tax seam + config `Tax:HomeCountry`/`Tax:FlatRate`.
- `src/Services/Ordering/Domain/Pricing.cs` — Why: Ordering pricing/tax math.
- `src/Storefront/components/checkout/CheckoutForm.tsx` (`estimateTax`, `EU_VAT_COUNTRIES`) — Why: hardcoded client tax to replace with storefront-config-driven.

**Admin**
- `src/Admin/Program.cs` (InteractiveServer line 10/55; middleware order lines 48-52 — `UseAntiforgery` before `UseAuthentication/UseAuthorization`) — Why: middleware order fix; render-mode config.
- `src/Admin/Components/App.razor` — Why: `blazor.web.js` bootstrap; where to add favicon / prerender handling.
- `src/Admin/Components/Pages/PaymentAccounts.razor` (line 80 `<select @bind="_new.Mode">`, body build line 114, error surface line 122) — Why: enum-as-string 500 + good error-surfacing pattern to replicate.
- `src/Services/Payments/Api/Endpoints/PaymentAccountAdminEndpoints.cs` (`CreatePaymentAccountRequest` lines 101-107, `Mode` is `PaymentProviderMode` enum) — Why: the enum the admin must send numerically (or add `JsonStringEnumConverter`).
- `src/Services/Entity/Api/Endpoints/EntityEndpoints.cs` (`ActivateSupplier` no body ~line 180; `SuspendSupplier(Guid id, SuspendSupplierRequest request,...)` ~line 195 requires body) — Why: empty-body 500.
- `src/Admin/Components/Pages/Entities.razor` — Why: supplier lifecycle buttons post empty; must send body + surface errors.
- `src/Admin/Components/Pages/CommerceOps.razor` (`HandleMutation` returns `Task<bool>`, `_error`/`_status`) — Why: canonical admin error/status pattern to standardize on.
- `src/Admin/Components/Pages/Ledger.razor` — Why: missing `@rendermode InteractiveServer`.
- `src/Admin/Services/GatewayClient.cs` (`PostAsync` line 68, `PutAsync`, `DeleteAsync`, `GetListAsync`) — Why: how admin calls the gateway; error surfacing happens at call sites.

### New Files to Create

- `src/Services/Catalog/Domain/VariantPrice.cs` — per-currency price child entity.
- `src/Services/Catalog/Infrastructure/Migrations/<ts>_VariantPerCurrencyPrices.cs` (+ Designer) — EF migration (generated).
- `src/Services/Ordering/Infrastructure/Migrations/<ts>_ProductCopyPerCurrencyPrices.cs` (+ Designer) — if ProductCopy stores per-currency (generated).
- `src/Storefront/lib/storefront.ts` — resolve active storefront config (currency/tax) + context helpers.
- `tests/3commerce.IntegrationTests/StorefrontCurrencyTests.cs` — per-currency catalog pricing + hide-when-missing.
- `src/Services/Catalog/tests/VariantPriceTests.cs` — domain unit tests for per-currency prices.
- `src/Storefront/e2e-admin/admin-actions.spec.ts` — Playwright that clicks admin actions and asserts a visible success/error (regression guard for C1/C2).

### Relevant Documentation — READ BEFORE IMPLEMENTING

- .NET minimal APIs & System.Text.Json enum binding: https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis/parameter-binding — Why: confirms enums bind as numbers by default (root of the 500s + seed bugs); `JsonStringEnumConverter` alternative.
- Blazor render modes & prerendering: https://learn.microsoft.com/aspnet/core/blazor/components/render-modes#prerendering — Why: `InteractiveServerRenderMode(prerender: false)` for the pre-circuit UX fix.
- Blazor Server middleware order (Auth → Authz → Antiforgery): https://learn.microsoft.com/aspnet/core/blazor/security/#antiforgery-support — Why: fix `Program.cs` ordering.
- EF Core owned/child collections + migrations: https://learn.microsoft.com/ef/core/modeling/owned-entities — Why: `VariantPrice` mapping.
- `docs/api/api_contracts_index.md` and `docs/api/catalog.openapi.json` / `ordering.openapi.json` — Why: regenerate after endpoint/contract changes (repo Rules in AGENTS.md).
- ADR-0015 (single store currency v1), ADR-0028 (Offer/supply), ADR-0024 (RLS) in `docs/adr/` — Why: this feature supersedes ADR-0015's single-currency assumption; note it.

### Patterns to Follow

**Money**: always integer minor units + ISO 4217 string (AGENTS.md invariant; `Variant.PriceMinor` comment Product.cs:110). Never floats.

**Enum-over-JSON**: minimal-API endpoints bind enums as **numbers** by default. Send numeric values from clients (see fixed dev-seed `scripts/dev-dummy-data.sh` entity/pricing/fulfillment payloads) OR add `JsonStringEnumConverter` at the service. Pick ONE approach and apply consistently for a given endpoint.

**Admin error surfacing** (canonical — `PaymentAccounts.razor` / `CommerceOps.razor`):
```csharp
_error = _status = null;
var response = await Gateway.PostAsync(path, body);
if (response.IsSuccessStatusCode) { _status = "..."; }
else { _error = await response.Content.ReadAsStringAsync(); }
```
Every admin action button MUST follow this (currently some, e.g. Entities lifecycle, do not).

**Blazor onclick with literal args**: single-quote the attribute — `@onclick='() => Act(id, "suspend")'` (not `\"`).

**Catalog admin write + publish**: mutate entity → `db.SaveChangesAsync` → `PublishUpsertedAsync(...)` (`AdminEndpoints.cs:246`). Per-currency prices must be included in the published event.

**Storefront data fetch**: server components call `gatewayFetch(path, { cache: "no-store" })` for dynamic/cookie data (`gateway.ts`); normalize numeric/string API shapes on read (see `normalizeAddressPurpose`, gateway.ts:124).

---

## IMPLEMENTATION PLAN

Grouped into 5 PRs (merge order = dependency order). Each PR: build + format + lint/tsc + tests + e2e, update `.ai-shared/plans/plan_status_executions.md` in the same change, regenerate OpenAPI/contract docs when endpoints change, commit on a feature branch, merge on green. **No AI/Co-Authored-By trailers** (standing rule).

### Phase 1 — Admin correctness (PR 1): surface errors + fix the two 500s + minors
Highest impact, self-contained, no data-model change.

### Phase 2 — Admin interaction UX (PR 2): pre-circuit render-mode fix

### Phase 3 — Storefront config foundation (PR 3): public storefront-config read endpoint + storefront reads it

### Phase 4 — Per-currency pricing (PR 4): Catalog data model → contract → admin editor → public API by currency → Ordering projection/checkout → storefront display (hide-when-missing) → importer

### Phase 5 — Storefront tax (PR 5): storefront-config-driven display + server-charged tax parity

---

## STEP-BY-STEP TASKS

Execute in order. Keys: CREATE / UPDATE / ADD / REMOVE / REFACTOR / MIRROR.

### PR 1 — Admin correctness

#### 1. UPDATE `src/Admin/Components/Pages/PaymentAccounts.razor`
- **IMPLEMENT**: send `mode` as the numeric `PaymentProviderMode` value (Test=1, Live=2 — confirm from `src/Services/Payments/Domain`), i.e. bind `_new.Mode` to an `int`/enum and send the number in the body (line 114). Keep the existing error surfacing (line 122).
- **PATTERN**: numeric-enum bodies as in fixed `scripts/dev-dummy-data.sh`.
- **GOTCHA**: `CreatePaymentAccountRequest.Provider` is a `string` (fine); only `Mode` is the enum. Verify enum member values before hardcoding.
- **VALIDATE**: `dotnet build src/Admin/3commerce.Admin.csproj -clp:ErrorsOnly` then live: create a payment account in the admin → expect 201 (not 500).

#### 2. UPDATE `src/Admin/Components/Pages/Entities.razor` (supplier lifecycle buttons)
- **IMPLEMENT**: `suspend` must POST a body `{ reason }` (endpoint requires `SuspendSupplierRequest`); audit `activate`/`archive`/`submit-verification`/`verification-complete` for required bodies and send them. Add a small reason input or default reason. Ensure every action surfaces `_error` on non-2xx.
- **PATTERN**: `EntityEndpoints.cs` `SuspendSupplier(Guid id, SuspendSupplierRequest request,...)` vs `ActivateSupplier(Guid id,...)` (no body).
- **GOTCHA**: empty body → "Implicit body inferred … no body provided" 500. Match each endpoint's exact request record.
- **VALIDATE**: live: suspend a supplier → expect 200 + visible status (not 500).

#### 3. UPDATE all admin action pages to surface API errors uniformly
- **IMPLEMENT**: audit every `@onclick` that calls `Gateway.Post/Put/Delete` across `Components/Pages/*.razor`; ensure each sets `_error`/`_status` from the response (or via `HandleMutation`). Add `_error`/`_status` display blocks where missing.
- **PATTERN**: `CommerceOps.razor` `HandleMutation` (`Task<bool>`) + `PaymentAccounts.razor` error block.
- **VALIDATE**: live click-through — a 4xx now shows a message instead of a silent no-op.

#### 4. UPDATE `src/Admin/Program.cs` — middleware order
- **IMPLEMENT**: reorder to `UseAuthentication()` → `UseAuthorization()` → `UseAntiforgery()` (currently Antiforgery precedes auth, lines 50-52). Keep `IpAllowlistMiddleware` + `UseStaticFiles` before auth.
- **VALIDATE**: `dotnet build`; login + a form POST still works; `dotnet format --verify-no-changes`.

#### 5. ADD `@rendermode InteractiveServer` to `src/Admin/Components/Pages/Ledger.razor`; ADD `favicon.ico`
- **IMPLEMENT**: add the directive (defensive — matches sibling pages); add a `favicon.ico` (or `<link rel="icon">` in `App.razor`) to kill the 404 noise.
- **VALIDATE**: build; no 404 for `/favicon.ico`.

#### 6. CREATE `src/Storefront/e2e-admin/admin-actions.spec.ts`
- **IMPLEMENT**: log in; for representative actions (payment-account create, supplier suspend, storefront transition, role save) click and assert a visible success **or** error message appears (not silent). Wait for the Blazor circuit before clicking.
- **PATTERN**: existing `e2e-admin` project + supplier test's `expect.toPass()` circuit-race handling.
- **VALIDATE**: `cd src/Storefront && npx playwright test --project=admin`.

### PR 2 — Admin interaction UX (pre-circuit)

#### 7. UPDATE admin interactive render mode to disable prerender (or add a connecting gate)
- **IMPLEMENT**: render interactive pages with `@rendermode="new InteractiveServerRenderMode(prerender: false)"` (or set once at the router in `Routes.razor`), so buttons are live the moment they render. Optionally add a lightweight "connecting…" indicator + Blazor reconnect UI in `App.razor`.
- **PATTERN**: Blazor render-modes docs (prerender:false).
- **GOTCHA**: disabling prerender delays first paint slightly; acceptable for an internal admin. Verify data-loading `OnInitializedAsync` still runs once (not twice).
- **VALIDATE**: live: immediately clicking a freshly-loaded page's button works; `admin-actions.spec.ts` passes without artificial waits.

### PR 3 — Storefront config foundation

#### 8. ADD public storefront-config read endpoint in Catalog
- **IMPLEMENT**: add an anonymous `GET /api/catalog/storefronts/by-slug/{slug}` (and/or `by-host/{host}`) returning `{ currency, taxRegime, taxRateBasisPoints, publicUrl, name, state }` for **Active/Preview** storefronts only. Do NOT put it under the admin group (`StorefrontEndpoints.cs:15-17` requires `AdminPolicy`); add a separate public group.
- **PATTERN**: `StorefrontEndpoints.cs` `ToResponse`/`StorefrontResponse` shape.
- **GOTCHA**: don't leak Draft/Archived or password hashes; return a minimal public DTO.
- **VALIDATE**: regenerate `docs/api/catalog.openapi.json` + `api_contracts_index.md`; `curl :8080/api/catalog/storefronts/by-slug/au` → AUD/GST config.

#### 9. CREATE `src/Storefront/lib/storefront.ts` + wire context
- **IMPLEMENT**: `getStorefrontConfig(slugOrHost)` (via `gatewayFetch`, cached per request); resolve the active storefront for `[storefront]` routes (slug) and the root app (host / `STORE_CURRENCY` fallback). Export a typed `StorefrontConfig { currency; taxRegime; taxRateBasisPoints; ... }`.
- **UPDATE** `src/Storefront/app/[storefront]/page.tsx` to use the real config instead of `LOCAL_STOREFRONT_LABELS`.
- **VALIDATE**: `cd src/Storefront && npm run lint && npx tsc --noEmit`; `/au` hero shows real config.

### PR 4 — Per-currency product pricing

#### 10. CREATE `src/Services/Catalog/Domain/VariantPrice.cs` + map it
- **IMPLEMENT**: `VariantPrice { Guid Id; Guid VariantId; string Currency; long PriceMinor; }`; add `ICollection<VariantPrice> Prices` to `Variant` (Product.cs:104). Keep `Variant.PriceMinor`/`Currency` as base/default + fallback source. Configure in `CatalogDbContext` with unique index `(VariantId, Currency)`.
- **GOTCHA**: preserve minor-units invariant; currency is 3-letter ISO upper.
- **VALIDATE**: `dotnet ef migrations add VariantPerCurrencyPrices -p src/Services/Catalog/Infrastructure -s src/Services/Catalog/Api`; `dotnet build`.

#### 11. UPDATE `ProductUpserted` contract + Catalog publish
- **IMPLEMENT**: add per-currency prices to `ProductVariantUpserted` (e.g. `IReadOnlyList<VariantCurrencyPrice> Prices`); populate in `AdminEndpoints.PublishUpsertedAsync` (line 246) and the importer path.
- **GOTCHA**: this is a shared contract — update ALL consumers (Ordering projection, any others) in the same PR to avoid deserialization breaks.
- **VALIDATE**: `dotnet build 3commerce.sln`.

#### 12. UPDATE Ordering `ProductCopy` projection to store per-currency prices
- **IMPLEMENT**: extend `ProductCopy`/`VariantCopy` (ProductCopy.cs) with per-currency prices; update the `ProductUpserted` consumer + migration.
- **VALIDATE**: `dotnet ef migrations add ProductCopyPerCurrencyPrices ...`; build; Ordering unit tests.

#### 13. UPDATE public catalog API to price for a requested currency
- **IMPLEMENT**: `Search` (ProductsEndpoints.cs:22) + `GetBySlug` (:40) accept a `currency` query param; return `MinPriceMinor`/variant prices for that currency; **omit products with no price in that currency** from search results (hide-when-missing). Update `ISearchProvider`/search query accordingly.
- **GOTCHA**: search min-price must be computed within the requested currency only.
- **VALIDATE**: `curl ':8080/api/catalog/products?currency=AUD'` → AUD prices, EUR-only products absent; OpenAPI regenerated.

#### 14. UPDATE admin product editor for per-currency prices
- **IMPLEMENT**: in `Catalog.razor`, per variant, allow add/edit/remove of `(currency, priceMinor)` rows (reuse `CurrencySelect`). Send them to the create/update product endpoints (`AdminEndpoints.cs`).
- **VALIDATE**: build; live: set AUD+EUR+USD prices on a product; GET shows them.

#### 15. UPDATE storefront to display storefront-currency price (hide-when-missing)
- **IMPLEMENT**: `searchProducts`/`getProduct` (gateway.ts) pass the resolved storefront `currency`; `ProductGrid`/`ProductCard`/PDP/cart render that currency's price. Product listing already excludes missing (server-side); PDP for a missing-currency product → 404/hidden.
- **UPDATE** `ProductCard.tsx:28` stays `formatMoney(minPriceMinor, currency)` but currency now = storefront currency.
- **VALIDATE**: lint/tsc; live `/au` shows A$ tenant-set prices; `/eu` €; `/us` US$.

#### 16. UPDATE checkout/cart to use per-currency price
- **IMPLEMENT**: cart + `CheckoutEndpoints` (line 54/62) price and revalidate against the storefront currency's `VariantPrice`/`ProductCopy` value; snapshot chosen currency.
- **VALIDATE**: MoneyFlow integration tests; live checkout on `/au` charges AUD.

#### 17. UPDATE importer + dev seed for multi-currency
- **IMPLEMENT**: importer accepts optional per-currency price columns; `scripts/dev-dummy-data.sh` seeds AUD/EUR/USD prices for demo products so `/au`,`/eu`,`/us` all show products.
- **VALIDATE**: `bash -n scripts/dev-dummy-data.sh`; `scripts/dev-dummy-data.sh --profile full` → demo products priced in all three.

### PR 5 — Storefront tax

#### 18. CREATE `src/Storefront/lib/tax.ts` (config-driven) + UPDATE `CheckoutForm.tsx`
- **IMPLEMENT**: compute tax from `StorefrontConfig.taxRegime`/`taxRateBasisPoints` (replace hardcoded `estimateTax`/`EU_VAT_COUNTRIES`). Per-regime inclusive/exclusive display rule (AU GST inclusive, EU VAT inclusive, US sales tax added).
- **VALIDATE**: lint/tsc; `/au` cart/checkout shows 10% GST.

#### 19. UPDATE server-charged tax to match storefront config
- **IMPLEMENT**: flow the storefront regime/rate into the Ordering/Payments tax computation (`Pricing.cs` / `ITaxStrategy` / `AuthorizePaymentConsumer`, config `Tax:*`) so the charged tax equals the displayed tax.
- **GOTCHA**: today `FlatRateTaxStrategy` uses global `Tax:HomeCountry`/`Tax:FlatRate`; make it storefront-aware.
- **VALIDATE**: Tax integration tests; live: displayed GST == order total tax.

---

## TESTING STRATEGY

### Unit Tests
- `src/Services/Catalog/tests/VariantPriceTests.cs` — add/replace per-currency price; invariants (unique currency, minor units, ISO code).
- Ordering projection tests — `ProductUpserted` with per-currency prices projects correctly.
- Tax rule tests — regime → rate → inclusive/exclusive amount.

### Integration Tests (`tests/3commerce.IntegrationTests`)
- `StorefrontCurrencyTests.cs` — create product with AUD+EUR prices; `GET /products?currency=AUD` returns AUD price; EUR-only product hidden for AUD.
- Extend `MoneyFlowTests` — checkout on an AUD storefront charges AUD + correct GST.
- Admin: payment-account create returns 201 (numeric mode); supplier suspend returns 200 with body.

### Edge Cases
- Product with a price in only some currencies (hidden on storefronts lacking it).
- Storefront currency changed after prices set (display updates; no stale cache).
- Currency with 0-decimal conventions (Intl formatting) — verify `formatMoney`.
- Suspend with empty reason (validation 400 surfaced, not 500).
- Rapid admin click immediately after navigation (no dead button after PR 2).

---

## VALIDATION COMMANDS

### Level 1: Syntax & Style
- `dotnet format --verify-no-changes` (or per touched csproj)
- `cd src/Storefront && npm run lint && npx tsc --noEmit`
- `bash -n scripts/dev-dummy-data.sh`

### Level 2: Unit Tests
- `dotnet test src/Services/Catalog/tests/3commerce.Catalog.Tests.csproj`
- `dotnet test src/Services/Ordering/tests/3commerce.Ordering.Tests.csproj`

### Level 3: Integration Tests
- `dotnet test tests/3commerce.IntegrationTests/3commerce.IntegrationTests.csproj`
- `cd src/Storefront && npx playwright test --project=admin`

### Level 4: Manual Validation (against `scripts/dev-up.sh --with-frontends --data full`)
- Admin: create payment account (201), suspend supplier (200), each shows status; buttons respond immediately after load.
- `curl :8080/api/catalog/storefronts/by-slug/au` → AUD/GST.
- `curl ':8080/api/catalog/products?currency=AUD'` → AUD prices; EUR-only products absent.
- `/au` shows A$ prices + GST; `/eu` €; `/us` US$; checkout charges storefront currency; displayed tax == charged tax.

### Level 5: Full regression
- `scripts/e2e-verify.sh`
- Regenerate + diff `docs/api/*.openapi.json` and `docs/api/api_contracts_index.md`.

---

## ACCEPTANCE CRITERIA

- [ ] Tenant can set an explicit price per currency per product in the admin.
- [ ] `/au` shows A$ (tenant-set), `/eu` €, `/us` US$ — no FX conversion.
- [ ] Products without a price in a storefront's currency are hidden on that storefront.
- [ ] Each storefront shows legally-correct tax from its `TaxRegime`/`TaxRateBasisPoints`; charged tax matches displayed tax.
- [ ] Payment-account create and supplier suspend return 2xx (no 500).
- [ ] Every admin action shows a success/error message (no silent no-ops).
- [ ] Admin buttons respond on the first click after page load.
- [ ] All validation commands pass; OpenAPI/contract docs regenerated; no regressions.

---

## COMPLETION CHECKLIST

- [ ] Tasks 1-19 completed in order across PRs 1-5.
- [ ] Each PR: build + format + lint/tsc + unit + integration + e2e green.
- [ ] `plan_status_executions.md` updated in each PR.
- [ ] Migrations added for Catalog + Ordering; applied locally.
- [ ] Manual validation on the live stack confirms currency, tax, and admin behavior.
- [ ] Merged on green (no AI/Co-Authored-By trailers).

---

## NOTES

- **Supersedes ADR-0015** (single store currency v1): record the move to tenant-authored per-currency prices; update `Variant.Currency` comment (Product.cs:110) and add/annotate an ADR.
- **Enum-as-string is a recurring theme** (seed 500s, admin payment-account 500). Consider a follow-up decision: adopt `JsonStringEnumConverter` platform-wide (readable APIs) vs. keep numeric enums everywhere. Out of scope here; fix the specific offenders numerically.
- **`json_get` in `scripts/dev-dummy-data.sh` is broken** (heredoc consumes stdin) — tracked separately; not required for this feature but affects seed fidelity.
- Per-currency pricing is the highest-risk workstream (shared `ProductUpserted` contract touches Ordering). Land contract + all consumers in the same PR (Task 11-12) to avoid bus deserialization breaks.
- **Confidence: 7/10** for one-pass success on PRs 1-3 and 5; **6/10** on PR 4 (per-currency) due to the cross-service contract/projection/checkout ripple — validate the contract change against every consumer before merging.
