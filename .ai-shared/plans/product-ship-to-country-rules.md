# Feature: Product Ship-To Country Rules (admin-gated, per-product, per-country tax & shipping)

The following plan should be complete, but it's important that you validate documentation and codebase patterns and task sanity before you start implementing. Pay special attention to naming of existing utils, types, and models. Import from the right files.

## Feature Description

Builds on the **ship-to-country allowlist** already implemented on branch `feat/ship-to-country-rules` (storefront-level `ShipToCountries` allowlist → checkout rejects unserved destinations). This feature adds a finer-grained, **per-product, per-country rule** layer:

- A tenant-scoped **feature switch** ("Require per-country ship rules on products"), toggled in the Admin portal. When ON, product create/update **requires** at least one ship rule (mandatory per-country selection at product creation).
- Each **ship rule** targets a destination country (ISO-2 alpha-2, or `*` meaning whole-world default) and declares two booleans:
  - `ChargeDestinationTax` — when `false`, the destination's tax is **not** charged for this product to that country (e.g. a product characteristic — weight/size/type/courier/area — means it's tax-exempt or covered).
  - `ShippingCovered` — when `true`, shipment is **already covered**, so no shipping is charged for that product to that country.
- **Checkout applies the rules to the real charge**: lines whose resolved rule has `ChargeDestinationTax=false` are excluded from the tax base; when every line's resolved rule has `ShippingCovered=true`, the order's shipping is waived.

The rules flow Catalog → Ordering via the existing `ProductUpserted` projection (ADR-0008: no cross-service queries), mirroring exactly how `ShipToCountries` flows via `StorefrontConfigChanged`.

## User Story

As a **store operator (admin)**
I want to **switch on mandatory per-country ship rules and set, per product, whether each destination is taxed and whether shipping is already covered**
So that **checkout charges the correct tax and shipping for products whose logistics/tax treatment varies by destination (courier-covered shipping, tax-exempt destinations, etc.)**.

## Problem Statement

The storefront allowlist is all-or-nothing per destination (ship / don't ship). It cannot express that a *particular product* to a *particular country* should skip destination tax or ship free because a courier/supplier already covers it. Tax and shipping at checkout are currently computed storefront-wide with no per-product/per-country override.

## Solution Statement

Add an owned, JSON-serialized `ShipRules` collection to the Catalog `Product`, gated by a new tenant-scoped `TenantCatalogSettings.RequireProductShipRules` switch enforced at the product write endpoints and surfaced as an admin toggle. Project the rules into Ordering's `ProductCopy` read model via `ProductUpserted`, and apply them in `CheckoutEndpoints.Checkout` when computing the tax base and shipping. Cover with Catalog unit tests, Ordering/integration tests (money math + projection), and a Playwright e2e for the admin gate + storefront reflection.

## Feature Metadata

**Feature Type**: New Capability (extends an in-progress Enhancement)
**Estimated Complexity**: High (touches domain, EF + migrations in two services, a cross-service contract, the checkout money path, admin UI, and 4 test layers)
**Primary Systems Affected**: Catalog (Domain/Infrastructure/Api/Admin UI), BuildingBlocks Contracts, Ordering (Domain/Infrastructure/Api checkout), IntegrationTests, Storefront e2e
**Dependencies**: None new. Reuses MassTransit, EF Core value converters, Blazor Admin, Playwright.

---

## CONTEXT REFERENCES

### Relevant Codebase Files — READ THESE BEFORE IMPLEMENTING

**Ship-to allowlist (the pattern to MIRROR — already on this branch, uncommitted):**
- `src/Services/Catalog/Domain/Storefront.cs` (lines 24-30 `ShipToCountries` prop; 87-118 `SetShipToCountries` normalize/validate) — MIRROR for `Product.SetShipRules`.
- `src/Services/Catalog/Infrastructure/CatalogDbContext.cs` (lines 92-101 CSV value converter + `ValueComparer`) — MIRROR the converter shape for JSON `ShipRules`.
- `src/BuildingBlocks/Contracts/Catalog/StorefrontConfigChanged.cs` (lines 14-18, `string[]? ShipToCountries = null` back-compat) — MIRROR optional field on `ProductUpserted`.
- `src/Services/Ordering/Infrastructure/Projections/StorefrontConfigConsumer.cs` (lines 21-33, `m.ShipToCountries?.ToList() ?? []`) — MIRROR projection null-coalescing.
- `src/Services/Ordering/Domain/StorefrontTaxCopy.cs` (lines 24-26 `ShipToCountries`) + `OrderingDbContext.cs` (lines 62-68 converter) — MIRROR on `ProductCopy`.
- `src/Services/Ordering/Api/Endpoints/CheckoutEndpoints.cs` (lines 71-84 allowlist guard; **105-126 the tax computation to modify**; 158-161 offerCopies load; 190-209 line build) — the checkout money path.
- `tests/3commerce.IntegrationTests/MoneyFlowTests.cs` (lines 66-110 `Checkout_rejects_a_ship_to_country_outside_the_storefront_allowlist`, trial-balance asserts) — MIRROR for money tests.
- `tests/3commerce.IntegrationTests/StorefrontTaxProjectionTests.cs` (lines 30-52 projection test + `PublishAsync`/`WaitForCopyAsync` helpers) — MIRROR for projection test.
- `src/Storefront/e2e/ship-to.spec.ts` (whole file — login, `auStore`, admin PUT, skip-when-unseeded) — MIRROR for `ship-rules.spec.ts`.

**Product create/update + admin UI:**
- `src/Services/Catalog/Api/Endpoints/AdminEndpoints.cs` (96-166 `CreateProduct`; 168-277 `UpdateProduct`; 326-346 `Validate`; 353-357 `ToEditorDto`; 374-389 DTOs) — where rules + the switch gate go.
- `src/Admin/Components/Pages/Catalog.razor` (whole file; editor form 62-150; `ProductEditor` 269-297; `Save` 228-258) — where the ship-rules fieldset + settings toggle go.
- `src/Admin/Components/Shared/Countries.cs` (`Common`, `All`, `NameOf`) — reuse for country pickers.
- `src/Admin/Resources/SharedResource.resx` (see the `Ops.ShipTo.*` keys just added ~line 113) — add `Catalog.ShipRules.*` keys.

**Ordering read model + projection:**
- `src/Services/Ordering/Domain/ProductCopy.cs` (whole; add `ShipRules`).
- `src/Services/Ordering/Infrastructure/Projections/ProductCopyConsumer.cs` (whole; project `ShipRules`).
- `src/Services/Ordering/Domain/Pricing.cs` (`TaxMode` enum 3-8; `HomeRegimeTaxStrategy` 110-139 for reference — **note checkout does NOT use PricingEngine**, it inlines tax at CheckoutEndpoints 105-126).

**Catalog contract producer:**
- `src/BuildingBlocks/Contracts/Catalog/ProductUpserted.cs` (whole; add `ShipRules`).
- `AdminEndpoints.PublishUpsertedAsync` (`AdminEndpoints.cs` 295-305) — add rules to the published event.

**Tests harness references:**
- `tests/3commerce.IntegrationTests/CatalogAdminTests.cs` (1-45 fixture/admin client, category seeding) — MIRROR for settings + mandatory-rule tests.
- `src/Services/Catalog/tests/ProductModelTests.cs` and `StorefrontLifecycleTests.cs` (105-146 the new ShipToCountries unit tests) — MIRROR for `Product.SetShipRules` unit tests.

### New Files to Create

- `src/Services/Catalog/Domain/TenantCatalogSettings.cs` — tenant settings entity carrying `RequireProductShipRules`.
- `src/Services/Catalog/Infrastructure/Migrations/<ts>_ProductShipRulesAndCatalogSettings.cs` (+ `.Designer.cs`, snapshot updated) — generated by `dotnet ef`.
- `src/Services/Ordering/Infrastructure/Migrations/<ts>_ProductCopyShipRules.cs` (+ `.Designer.cs`, snapshot updated) — generated by `dotnet ef`.
- `src/Services/Catalog/tests/ProductShipRuleTests.cs` — unit tests for `ProductShipRule`/`SetShipRules`.
- `tests/3commerce.IntegrationTests/ProductShipRuleTests.cs` — integration: settings gate on create + rules projected.
- `src/Storefront/e2e/ship-rules.spec.ts` — Playwright e2e for the admin feature switch + product-form gate.

(`ProductShipRule` value object lives inside `Product.cs`; an Ordering-side copy inside `ProductCopy.cs`; a contract DTO inside `ProductUpserted.cs`. No separate files for those.)

### Patterns to Follow

**Money is integer minor units** (AGENTS.md invariant) — never float. Tax rounding in checkout uses `Math.Round(..., MidpointRounding.AwayFromZero)` (CheckoutEndpoints 119/124).

**Domain validation** throws `CatalogRuleException` (Catalog) — see `SetShipToCountries`. Normalize country codes: `raw.Trim().ToUpperInvariant()`; accept 2×`A-Z`, plus the sentinel `*`.

**EF collection persisted via converter + `ValueComparer`** — copy the exact `ValueComparer<List<T>>` shape from `CatalogDbContext.cs:97-100`. For `ShipRules` use JSON: `System.Text.Json.JsonSerializer.Serialize/Deserialize` with a `List<ProductShipRule>` (records compare structurally, so the comparer can `SequenceEqual`).

**Back-compat contract fields** are trailing optional params defaulting to `null` (see `StorefrontConfigChanged`/`ProductUpserted`). Consumers null-coalesce: `?.ToList() ?? []`.

**Admin resx**: every visible string + `.Tip` companion. Mirror `Ops.ShipTo.*` keys.

**EF migration gotcha (MEMORY):** after `dotnet ef migrations add`, run `dotnet format` on the Infrastructure csproj (the generated files fail the analyzer gate otherwise).

**Tenant resolution** in Catalog admin endpoints: `request.TenantId ?? DefaultTenantId(config)` where `DefaultTenantId` reads `Tenancy:DefaultTenantId` (AdminEndpoints 348-351). Reuse for settings.

---

## IMPLEMENTATION PLAN

### Phase 1: Catalog Domain (foundation)

Introduce the value object, the product collection with validation, and the settings entity. No persistence yet.

### Phase 2: Catalog Infrastructure (persistence + migration)

Map `Product.ShipRules` (JSON) and `TenantCatalogSettings`; generate the migration; format.

### Phase 3: Catalog API (endpoints + gate)

Settings GET/PUT; thread `ShipRules` through `ProductWriteRequest`/`ProductEditorDto`; enforce the mandatory gate; publish rules on `ProductUpserted`.

### Phase 4: Contract + Ordering projection

Extend `ProductUpserted`; add `ProductCopy.ShipRules` (+ migration); project it; **apply rules in checkout tax/shipping**.

### Phase 5: Admin UI

Ship-rules fieldset in the product editor + the feature-switch toggle; resx.

### Phase 6: Tests & Playwright validation

Unit + integration + projection + money tests; e2e spec; run everything.

---

## STEP-BY-STEP TASKS

Execute in order. Each task is atomic and independently testable.

### 1. ADD `ProductShipRule` + `ShipRules` + `SetShipRules` to `src/Services/Catalog/Domain/Product.cs`
- **IMPLEMENT**: `public sealed record ProductShipRule(string CountryCode, bool ChargeDestinationTax, bool ShippingCovered);` at end of file. On `Product`: `public List<ProductShipRule> ShipRules { get; private set; } = [];` and:
  `public void SetShipRules(IEnumerable<ProductShipRule>? rules, DateTimeOffset now)` — if `rules is null` return (leave as-is, MIRROR SetShipToCountries null-keeps semantics); else normalize each `CountryCode` = `Trim().ToUpperInvariant()`, allow `"*"` OR exactly 2 chars `A-Z` else throw `CatalogRuleException`; dedupe by `CountryCode` (last wins); sort with `*` first then ordinal; assign; set `UpdatedAt = now`.
- **PATTERN**: `Storefront.SetShipToCountries` (`Storefront.cs:92-118`).
- **GOTCHA**: `Product.UpdatedAt` is a settable prop (line 33) — assign directly, no method needed. Keep `ProductShipRule` a `record` so EF `ValueComparer.SequenceEqual` works structurally.
- **VALIDATE**: `dotnet build src/Services/Catalog/Api`

### 2. CREATE `src/Services/Catalog/Domain/TenantCatalogSettings.cs`
- **IMPLEMENT**: `public class TenantCatalogSettings { public Guid TenantId { get; init; } public bool RequireProductShipRules { get; set; } public DateTimeOffset UpdatedAt { get; set; } }`
- **PATTERN**: simple entity like `Category` (`Product.cs:137-145`).
- **VALIDATE**: `dotnet build src/Services/Catalog/Api`

### 3. UPDATE `src/Services/Catalog/Infrastructure/CatalogDbContext.cs`
- **IMPLEMENT**: add `public DbSet<TenantCatalogSettings> TenantCatalogSettings => Set<TenantCatalogSettings>();`. In the `Product` entity block, map `product.Property(p => p.ShipRules).HasColumnType("jsonb").HasConversion(v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null), v => JsonSerializer.Deserialize<List<ProductShipRule>>(v, (JsonSerializerOptions?)null) ?? new(), new ValueComparer<List<ProductShipRule>>((a,b)=>a!.SequenceEqual(b!), v=>v.Aggregate(0,(h,r)=>HashCode.Combine(h,r.GetHashCode())), v=>v.ToList()));`. Add an entity block: `modelBuilder.Entity<TenantCatalogSettings>(s => { s.ToTable("TenantCatalogSettings"); s.HasKey(x => x.TenantId); });`
- **IMPORTS**: `using System.Text.Json;` (ChangeTracking already imported).
- **PATTERN**: `Attributes`/`ImageUrls` use `jsonb` (`CatalogDbContext.cs:47-48`); ShipToCountries converter (92-101).
- **GOTCHA**: existing `Attributes` uses plain `.HasColumnType("jsonb")` with EF's built-in dictionary comparer; for a `List<record>` supply the explicit `ValueComparer` to avoid EF warning/incorrect change detection.
- **VALIDATE**: `dotnet build src/Services/Catalog/Infrastructure`

### 4. GENERATE Catalog migration
- **IMPLEMENT**: `dotnet ef migrations add ProductShipRulesAndCatalogSettings --project src/Services/Catalog/Infrastructure --startup-project src/Services/Catalog/Api` then `dotnet format src/Services/Catalog/Infrastructure/ThreeCommerce.Catalog.Infrastructure.csproj`.
- **GOTCHA**: MEMORY `3commerce-ef-migration-format` — the format step is mandatory. Verify the migration adds `ShipRules jsonb NOT NULL default '[]'` on `Storefronts`... **no** — on `Products`; and creates `TenantCatalogSettings`.
- **VALIDATE**: `dotnet build src/Services/Catalog/Api`

### 5. UPDATE `src/Services/Catalog/Api/Endpoints/AdminEndpoints.cs` — DTOs + persistence
- **IMPLEMENT**:
  - Add `public record ProductShipRuleDto(string CountryCode, bool ChargeDestinationTax, bool ShippingCovered);`
  - `ProductWriteRequest` += trailing `List<ProductShipRuleDto>? ShipRules = null`.
  - `ProductEditorDto` += `List<ProductShipRuleDto> ShipRules` (place before `ProductType` or append; update `ToEditorDto` to map `p.ShipRules.Select(r => new ProductShipRuleDto(r.CountryCode, r.ChargeDestinationTax, r.ShippingCovered)).ToList()`).
  - In `CreateProduct` and `UpdateProduct`, after building/updating the product call `product.SetShipRules(request.ShipRules?.Select(r => new ProductShipRule(r.CountryCode, r.ChargeDestinationTax, r.ShippingCovered)), now)`.
- **PATTERN**: how `Status`/`ProductType` optional fields are threaded (AdminEndpoints 132-133, 207-208, 387).
- **VALIDATE**: `dotnet build src/Services/Catalog/Api`

### 6. ADD settings endpoints + mandatory gate in `AdminEndpoints.cs`
- **IMPLEMENT**:
  - `group.MapGet("/settings", GetSettings); group.MapPut("/settings", UpdateSettings);` in `MapAdmin`.
  - `GetSettings(CatalogDbContext db, IConfiguration config, Guid? tenantId, CancellationToken ct)` → resolve tenant (`tenantId ?? DefaultTenantId(config)`), find-or-default `TenantCatalogSettings`, return `Ok(new CatalogSettingsResponse(tenant, settings?.RequireProductShipRules ?? false))`.
  - `UpdateSettings(CatalogSettingsRequest request, CatalogDbContext db, IAuditRecorder audit, ClaimsPrincipal user, IConfiguration config, TimeProvider time, CancellationToken ct)` → upsert row, set flag + `UpdatedAt`, audit `catalog.settings.update`, save, return `Ok`.
  - `record CatalogSettingsResponse(Guid TenantId, bool RequireProductShipRules); record CatalogSettingsRequest(Guid? TenantId, bool RequireProductShipRules);`
  - In BOTH `CreateProduct` and `UpdateProduct`, after tenant resolsolution add: `var settings = await db.TenantCatalogSettings.FindAsync([tenantId], cancellationToken); if (settings?.RequireProductShipRules == true && (request.ShipRules is null || request.ShipRules.Count == 0)) return TypedResults.ValidationProblem(new Dictionary<string,string[]>{ ["ShipRules"] = ["At least one per-country ship rule is required."] });`
- **PATTERN**: audit + endpoint shape mirror `CreateProduct`; auth is the group-level `AdminPolicy`.
- **GOTCHA**: `Validate(request)` returns `ValidationProblem?` and the create/update return unions already include `ValidationProblem` — reuse. Keep the gate AFTER slug/category validation to keep messages stable, or before — either is fine; put it right after `tenantId` is known.
- **VALIDATE**: `dotnet build src/Services/Catalog/Api`

### 7. UPDATE `AdminEndpoints.PublishUpsertedAsync` + `src/BuildingBlocks/Contracts/Catalog/ProductUpserted.cs`
- **IMPLEMENT**: `ProductUpserted` += trailing `IReadOnlyList<ProductShipRuleContract>? ShipRules = null`; add `public record ProductShipRuleContract(string CountryCode, bool ChargeDestinationTax, bool ShippingCovered);`. In `PublishUpsertedAsync` pass `product.ShipRules.Select(r => new ProductShipRuleContract(r.CountryCode, r.ChargeDestinationTax, r.ShippingCovered)).ToList()`. Change `PublishUpsertedAsync` to take the product (already does).
- **PATTERN**: `StorefrontConfigChanged` back-compat trailing param.
- **GOTCHA**: use a distinct contract type name (`ProductShipRuleContract`) — Contracts must not reference Catalog.Domain.
- **VALIDATE**: `dotnet build src/BuildingBlocks/Contracts && dotnet build src/Services/Catalog/Api`

### 8. UPDATE `src/Services/Ordering/Domain/ProductCopy.cs`
- **IMPLEMENT**: add `public List<ProductShipRule> ShipRules { get; set; } = [];` and `public sealed record ProductShipRule(string CountryCode, bool ChargeDestinationTax, bool ShippingCovered);` in the Ordering.Domain namespace. Add a helper `public ProductShipRule? RuleFor(string country)` → first `CountryCode == country` else `CountryCode == "*"` else null (country pre-uppercased by caller).
- **VALIDATE**: `dotnet build src/Services/Ordering/Infrastructure`

### 9. UPDATE `src/Services/Ordering/Infrastructure/OrderingDbContext.cs`
- **IMPLEMENT**: in the `ProductCopy` entity config (find `modelBuilder.Entity<ProductCopy>`), map `ShipRules` with the same JSON converter + `ValueComparer<List<ProductShipRule>>` as Catalog task 3.
- **IMPORTS**: `using System.Text.Json;` (ChangeTracking already imported per the ShipToCountries diff).
- **GOTCHA**: confirm the `ProductCopy` entity block exists; if `ProductCopy` is mapped by convention only, add a block `modelBuilder.Entity<ProductCopy>(p => { ... })`.
- **VALIDATE**: `dotnet build src/Services/Ordering/Infrastructure`

### 10. GENERATE Ordering migration
- **IMPLEMENT**: `dotnet ef migrations add ProductCopyShipRules --project src/Services/Ordering/Infrastructure --startup-project src/Services/Ordering/Api` then `dotnet format src/Services/Ordering/Infrastructure/ThreeCommerce.Ordering.Infrastructure.csproj`.
- **VALIDATE**: `dotnet build src/Services/Ordering/Api`

### 11. UPDATE `src/Services/Ordering/Infrastructure/Projections/ProductCopyConsumer.cs`
- **IMPLEMENT**: on both create + update branches set `copy.ShipRules = m.ShipRules?.Select(r => new ProductShipRule(r.CountryCode, r.ChargeDestinationTax, r.ShippingCovered)).ToList() ?? [];`
- **PATTERN**: `StorefrontConfigConsumer` null-coalescing (lines 24/33 of the diff).
- **VALIDATE**: `dotnet build src/Services/Ordering/Infrastructure`

### 12. APPLY rules in `src/Services/Ordering/Api/Endpoints/CheckoutEndpoints.cs`
- **IMPLEMENT**: After the allowlist guard (line ~84) and before/around the tax block (105-126):
  - Load rules-bearing copies: `var shipRuleCopies = await db.ProductCopies.AsNoTracking().Where(p => productIds.Contains(p.ProductId)).ToListAsync(ct);` — but `productIds` is computed later (158). MOVE the `productIds` computation up, or compute a local `var cartProductIds = cart.Items.Select(i => i.ProductId).Distinct().ToList();` here. Load copies as entities (NOT `.Select`) so the JSON converter materializes (MIRROR the allowlist comment at 77-78).
  - For each cart item resolve `var rule = copies.FirstOrDefault(c => c.ProductId == i.ProductId)?.RuleFor(shipToCountry);`
  - `taxableSubtotal = sum over items where rule is null || rule.ChargeDestinationTax of UnitPriceMinor*Quantity`.
  - `allCovered = cart.Items.All(i => copy?.RuleFor(shipToCountry)?.ShippingCovered == true)` (all items have a covered rule) → if true set `shippingMinor = 0` **before** it feeds `baseMinor`/the intent.
  - Change the tax base from `subtotal` to `taxableSubtotal`: `var taxBaseGoods = taxableSubtotal;` then `baseMinor = taxBaseGoods - discountMinor + shippingMinor;` (discount stays 0 here). Keep the inclusive/exclusive branches identical otherwise.
  - **Keep `netMinor`/`GrossMinor` semantics** so `AuthorizePayment` receives the adjusted total. `CheckoutResponse` still returns `subtotal` (full goods subtotal) as `NetMinor` — verify existing field meaning: response `NetMinor` is `subtotal` (full), tax is the reduced tax. That's consistent (subtotal is pre-tax goods; tax reflects exemptions).
- **PATTERN**: existing inline tax at 105-126; allowlist entity-load comment 77-80.
- **GOTCHA**: Money invariant — the trial balance test (`MoneyFlowTests`) requires ledger to net to 0; ensure `intent.GrossMinor` (from Payments) equals `netMinor` you send. You send `netMinor` in `AuthorizePayment` (line 145) and store `intent.GrossMinor`. Do NOT double-adjust. Only change how `taxMinor`/`shippingMinor`/`baseMinor` are computed; the flow after is unchanged.
- **GOTCHA**: when `allCovered` waives shipping, the earlier `request.SelectedShippingAmountMinor` validation still applies — waive AFTER those guards (put the waive right before `baseMinor` computation at 115).
- **VALIDATE**: `dotnet build src/Services/Ordering/Api`

### 13. UPDATE Admin UI `src/Admin/Components/Pages/Catalog.razor`
- **IMPLEMENT**:
  - `@using ThreeCommerce.Admin.Components.Shared` (for `Countries`) if not present.
  - In list view header add a feature-switch row: a checkbox bound to `_requireShipRules` with a Save button calling `SaveSettings()` (PUT `/api/catalog/admin/settings` `{ requireProductShipRules = _requireShipRules }`). Load via `GET /api/catalog/admin/settings` in `OnInitializedAsync`.
  - In the editor, add a `<fieldset>` "Ship rules" listing `_editing.ShipRules` rows: a country `<select>` (options from `Countries.Common` + a `*` "All countries" option), a `ChargeDestinationTax` checkbox, a `ShippingCovered` checkbox, remove button; plus an "Add rule" button. When `_requireShipRules` is true, show a hint that it's mandatory and block `Save()` client-side if empty (set `_error`).
  - Extend `ProductEditor` with `public List<ShipRuleEditor> ShipRules { get; set; } = [];` and a `ShipRuleEditor { string CountryCode="*"; bool ChargeDestinationTax=true; bool ShippingCovered; }`. Ensure `EditProduct` maps loaded rules (the `GET` DTO returns them).
- **PATTERN**: `CommerceOps.razor` multi-select + `_editShipTo` state; existing Catalog fieldset markup (Variants/Images 86-144).
- **VALIDATE**: `dotnet build src/Admin`

### 14. ADD resx strings `src/Admin/Resources/SharedResource.resx`
- **IMPLEMENT**: keys + `.Tip`: `Catalog.ShipRules`, `Catalog.ShipRules.Hint`, `Catalog.ShipRule.Country`, `Catalog.ShipRule.ChargeTax`, `Catalog.ShipRule.ShippingCovered`, `Catalog.ShipRule.Add`, `Catalog.ShipRule.Remove.Tip`, `Catalog.ShipRules.Required`, `Catalog.Settings.RequireShipRules`, `Catalog.Settings.RequireShipRules.Tip`, `Catalog.Settings.Save`, `Catalog.ShipRule.AllCountries`.
- **PATTERN**: `Ops.ShipTo.*` keys.
- **VALIDATE**: `dotnet build src/Admin`

### 15. CREATE `src/Services/Catalog/tests/ProductShipRuleTests.cs`
- **IMPLEMENT**: `SetShipRules` — default empty; normalize/uppercase/dedupe/sort with `*` first; reject non-2-letter non-`*`; null keeps current; empty list clears.
- **PATTERN**: `StorefrontLifecycleTests.cs:105-146`.
- **VALIDATE**: `dotnet test src/Services/Catalog/tests --filter FullyQualifiedName~ProductShipRule`

### 16. CREATE `tests/3commerce.IntegrationTests/ProductShipRuleTests.cs`
- **IMPLEMENT**: (a) PUT `/admin/settings` require=true → POST `/admin/products` without rules ⇒ 400 (ValidationProblem `ShipRules`); with a rule ⇒ 201. (b) settings GET reflects the flag. Reset settings to false in teardown.
- **PATTERN**: `CatalogAdminTests.cs` fixture + admin client + category seed.
- **VALIDATE**: `dotnet test tests/3commerce.IntegrationTests --filter FullyQualifiedName~ProductShipRule`

### 17. ADD projection + money tests
- **IMPLEMENT**:
  - In `StorefrontTaxProjectionTests.cs` (or a new `ProductCopyShipRuleProjectionTests`): publish `ProductUpserted` with `ShipRules` and assert the `ProductCopy.ShipRules` projects; empty clears.
  - In `MoneyFlowTests.cs`: `Checkout_skips_destination_tax_when_product_rule_exempts_the_country` and `Checkout_waives_shipping_when_all_lines_are_shipping_covered` — seed a `ProductCopy` (or `StorefrontTaxCopy` for the tax regime) + rule, drive `/checkout`, assert `taxMinor`/`shippingMinor` on `CheckoutResponse`, and assert `fixture.TrialBalanceAsync() == 0`.
- **PATTERN**: `MoneyFlowTests.cs:66-110`; `StorefrontTaxProjectionTests.cs:33-52`.
- **VALIDATE**: `dotnet test tests/3commerce.IntegrationTests --filter "FullyQualifiedName~ShipRule|FullyQualifiedName~ship_to_country"`

### 18. CREATE `src/Storefront/e2e/ship-rules.spec.ts`
- **IMPLEMENT**: login as admin (MIRROR `ship-to.spec.ts`); PUT `/api/catalog/admin/settings` require=true; POST a product without rules ⇒ expect 400; POST with a `*` rule (`chargeDestinationTax:false`) ⇒ 201; (optional) drive the storefront checkout for that product and assert the tax line reads 0. `finally` PUT settings back to require=false and delete the created product. `test.skip` when the seed/store is absent, matching `ship-to.spec.ts`.
- **PATTERN**: `ship-to.spec.ts` (login, GATEWAY, skip-guard, restore-in-finally).
- **VALIDATE**: `bash scripts/e2e-verify.sh` (or the project's Playwright runner — see task 19).

### 19. RUN full validation (build, format, unit, integration, e2e) — see VALIDATION COMMANDS.

---

## TESTING STRATEGY

### Unit Tests (`src/Services/Catalog/tests`, xUnit)
`Product.SetShipRules`: empty default; normalize/dedupe/sort (`*` first); reject bad codes; null-keeps; empty-clears. Fast, no DB.

### Integration Tests (`tests/3commerce.IntegrationTests`, `[Category=Integration]`, Testcontainers Postgres)
- Settings gate: mandatory switch forces `ShipRules` at product create (400 vs 201).
- Projection: `ProductUpserted.ShipRules` → `ProductCopy.ShipRules` (set + clear).
- Money: destination tax skipped for an exempt product/country; shipping waived when all lines covered; **`TrialBalanceAsync()==0`** in both.

### e2e (Playwright, `src/Storefront/e2e`)
Admin feature switch + product-form gate through the gateway; restore state in `finally`; skip when demo seed absent (CI browser-e2e boots via importer only).

### Edge Cases
- `*` whole-world rule vs a specific-country rule (specific wins in `RuleFor`).
- All lines exempt → tax base 0 but shipping (if charged) still taxed under exclusive regime.
- Mixed cart: some lines covered, some not → shipping NOT waived (all-covered rule).
- Older publisher: `ProductUpserted` with null `ShipRules` → `ProductCopy.ShipRules` empty, checkout unchanged (worldwide/taxed).
- Inclusive regime (AU GST / EU VAT): exempt line removed from the contained-tax base; shopper still pays listed price for taxable lines.
- Feature switch OFF → rules optional; still applied at checkout if present.

---

## VALIDATION COMMANDS

### Level 1: Syntax & Style
```
dotnet build 3commerce.sln
dotnet format src/Services/Catalog/Infrastructure/ThreeCommerce.Catalog.Infrastructure.csproj --verify-no-changes
dotnet format src/Services/Ordering/Infrastructure/ThreeCommerce.Ordering.Infrastructure.csproj --verify-no-changes
cd src/Storefront && npm run lint && npx tsc --noEmit
```

### Level 2: Unit Tests
```
dotnet test src/Services/Catalog/tests
dotnet test src/Services/Ordering/tests
```

### Level 3: Integration Tests
```
dotnet test tests/3commerce.IntegrationTests --filter "Category=Integration&(FullyQualifiedName~ShipRule|FullyQualifiedName~MoneyFlow|FullyQualifiedName~CatalogAdmin|FullyQualifiedName~StorefrontTaxProjection)"
```

### Level 4: Manual / e2e Validation
```
bash scripts/dev-up.sh --data full      # boot stack + AU demo seed
cd src/Storefront && npx playwright test e2e/ship-rules.spec.ts e2e/ship-to.spec.ts
bash scripts/dev-down.sh
```
Manual: Admin → Catalog → toggle "Require per-country ship rules" → New product with no rule ⇒ save blocked; add a `*` rule `ChargeDestinationTax=false` ⇒ saves; shop that product on the storefront ⇒ checkout tax line shows 0.

### Level 5: Additional
`bash scripts/doctor.sh` (stack health); `bash scripts/e2e-verify.sh` if present.

---

## ACCEPTANCE CRITERIA

- [ ] Tenant feature switch persists and is toggleable via Admin + `PUT /api/catalog/admin/settings`.
- [ ] With the switch ON, product create/update without `ShipRules` returns 400; with rules returns 201/200.
- [ ] `ProductShipRule` normalizes/dedupes/sorts and rejects invalid codes (unit-tested).
- [ ] Rules project Catalog→Ordering via `ProductUpserted` (set + clear).
- [ ] Checkout excludes exempt lines from the tax base and waives shipping when all lines are covered; `TrialBalanceAsync()==0`.
- [ ] Admin product editor edits rules; storefront/checkout reflects them.
- [ ] Level 1–3 commands pass with zero errors; e2e passes (or skips cleanly where unseeded).
- [ ] No regression in existing ship-to allowlist tests or MoneyFlow trial balance.
- [ ] Back-compatible: older events with null rules behave as before.

---

## COMPLETION CHECKLIST

- [ ] All tasks 1–19 completed in order, each validation passing.
- [ ] Two migrations generated + `dotnet format`-ed; snapshots updated.
- [ ] Full solution builds; TS lint + typecheck clean.
- [ ] Unit + integration suites green; trial balance holds.
- [ ] Playwright spec added and passing/skipping cleanly.
- [ ] Admin strings localized (key + `.Tip`).
- [ ] Reviewed for the money invariant (integer minor units; no double-adjust of the intent total).

---

## NOTES

**Design decisions & trade-offs:**
- **JSON column (not child table)** for `ShipRules` — mirrors the branch's CSV converter approach for `ShipToCountries`, keeps migration surface small, and the rules are always read as a whole per product (no per-rule querying needed). `jsonb` matches `Attributes`/`ImageUrls`.
- **Feature switch is tenant-scoped** (new `TenantCatalogSettings`, PK=TenantId) because Products are tenant-scoped, not storefront-scoped — a per-storefront switch would be ambiguous for a product shared across storefronts. Minimal one-column table; no tenant-settings framework invented.
- **Checkout inlines the money math** (does not use `PricingEngine`) — so enforcement lives in `CheckoutEndpoints`, adjusting the *taxable* subtotal and shipping. `PricingLineInput.TaxMode.Exempt` already exists for the engine path; this feature deliberately targets the actual charge path.
- **Shipping waive rule = all lines covered** — order-level flat shipping can't be partially attributed cleanly; "all covered ⇒ free" is the defensible, testable rule. Documented for reviewers; can later evolve to per-parcel once Fulfillment quotes are per-line.
- **`*` sentinel = whole-world default**, specific country overrides it (`RuleFor` precedence) — matches the user's "specific area / whole country" framing.
- Rules are **advisory to tax/shipping only** — they never widen the ship-to allowlist; a country still must pass the storefront allowlist guard first.

**Confidence: 8/10** for one-pass success. Main risks: (1) the checkout money-math edit must not disturb the trial-balance invariant — mitigated by keeping the intent-total flow untouched and only recomputing tax/shipping inputs; (2) EF `ValueComparer` for `List<record>` must be explicit or change-tracking misbehaves; (3) e2e depends on the `--data full` demo seed (guarded with `test.skip`).
