# Feature: threshold-promotions

The following plan should be complete, but its important that you validate documentation and codebase patterns and task sanity before you start implementing.

Pay special attention to naming of existing utils types and models. Import from the right files etc.

## Feature Description

**Threshold-based promotions**: a tenant-authored, storefront- or product-scoped rule that grants **free
shipping** and/or a **discount** (percentage OR fixed amount) once the cart clears a **threshold** measured
in **money** and/or **quantity**.

- **Threshold types** — `$` amount and/or quantity. When both are set they are **AND**ed (both must be met).
- **Scope** —
  - `Storefront` scope measures the **whole cart's item value** (or the cart's total unit count) and, when it
    wins, discounts **all** item lines.
  - `Product` scope measures **the same product's** total item value (or that product's quantity) and, when it
    wins, discounts **only that product's lines**.
- **Comparison base (critical)** — the **offer-resolved effective selling price** (the effective `Offer` price
  when one applies for this storefront/currency/window, otherwise the catalog price) `× quantity`,
  **excluding taxes, shipping and fees**. Supplier cost (COGS) is irrelevant — it is the cost side, never the
  customer-facing base. The **same base** is what a won discount is applied to.
- **Rewards** — free shipping and/or a discount, where the discount is either a percentage (0–100) or a fixed
  minor-unit amount (denominated in the promotion's currency).
- **Combinability** — every promotion carries a flag: `Exclusive` (only it applies) or `Combinable` (stacks
  with other combinable promotions). The engine evaluates **all** eligible promotions, then takes the better of
  `[best single exclusive]` vs `[sum of all combinable]`, where "better" = greatest **customer benefit** =
  `discountMinor + (freeShippingApplied ? shippingMinor : 0)`.
- **Relation to the storefront-wide discount (PR #256, commit `2010d24`)** — that is a **separate storefront
  setting, not a promotion**. It is always applied and the combinability flag never governs it; the flag
  governs promotion-vs-promotion only.

The feature deliberately **extends the dormant promotion engine that already exists** in
`src/Services/Ordering/Domain/Pricing.cs` rather than building a second one, and fixes the fact that the
engine is currently **dead code in production** (see Solution Statement / Phase 3).

## User Story

As a **store operator (tenant admin)**
I want to **create threshold promotions that give shoppers free shipping and/or a discount once their cart (or
their quantity of one product) crosses a money or quantity threshold, and to say whether each promotion is
exclusive or stacks with others**
So that **I can run "spend $100, ship free", "buy 3, get 15% off", and "spend $200, take $20 off" campaigns
that raise average order value, with a deterministic, auditable rule for which promotion wins when several
qualify.**

## Problem Statement

1. **The platform has no promotions.** There is a rich `PromotionKind` enum, a `Promotion` record with
   eligibility + best-rule selection + free-shipping zeroing in `src/Services/Ordering/Domain/Pricing.cs:10-279`
   — and **nothing feeds it**. There is no `Promotion` entity, no table, no admin UI, no contract, and
   `CheckoutEndpoints` never constructs a `Promotion` nor passes one in
   (`src/Services/Ordering/Api/Endpoints/CheckoutEndpoints.cs:29-393` never mentions `Promotion` or
   `PricingEngine`). Only the unit tests (`src/Services/Ordering/tests/PricingTests.cs`) exercise it.
2. **The existing engine cannot express this feature.** `Promotion` has `MinimumQuantity` but **no money
   threshold** (`Pricing.cs:44-56`), and **no combinability flag** — `PricingEngine.Price` hard-codes
   "exactly one promotion wins" via `.OrderByDescending(...).FirstOrDefault()` (`Pricing.cs:158-164`), and
   `PricingResult` can only report a single `AppliedPromotionId` (`Pricing.cs:82-90`).
3. **CRITICAL — `PricingEngine` is not on the production money path.** `CheckoutEndpoints.Checkout`
   **re-implements the whole money computation inline**: subtotal (`CheckoutEndpoints.cs:162`), ship-rule tax
   exemption (`:165-167`), shipping waiver (`:169`), the storefront discount (`:171-180`), proportional
   taxable-discount allocation (`:258-263`), inclusive vs exclusive tax (`:264-275`) and
   `chargeBaseMinor`/`netMinor`. Extending only `Pricing.cs` would leave `PricingTests` **green while checkout
   charges the wrong amount** — the single largest one-pass-failure risk in this feature.
4. **Checkout must not query Catalog** (ADR-0008: no cross-service reads). Any Catalog-owned promotion must
   reach Ordering as a projected read copy, or checkout cannot see it.
5. **Shown ≠ charged risk.** The storefront cart holds the *catalog* price captured at add-time
   (`src/Services/Ordering/Api/Endpoints/CartEndpoints.cs:74-80,195-203`); checkout re-resolves the *offer*
   price (`CheckoutEndpoints.cs:101-129`). A promotion preview computed from the raw cart would disagree with
   the charge.

## Solution Statement

Mirror, **file for file**, the merged storefront-wide-discount slice (commit `2010d24`, PR #256) — the closest
precedent in the repo — combined with the `Offer → OfferChanged → OfferCopy` projection pattern:

1. **Catalog owns the `Promotion` aggregate.** New `Promotion` entity + `/admin/promotions` endpoints, next to
   `Storefront` and `Offer`, which it references. (Justification in NOTES: the `Pricing` service is a dormant
   Phase-7 `Price` aggregate — `src/Services/Pricing/Domain/Price.cs:16-25` — with no bus wiring and no
   checkout path; routing through it would need a brand-new service-to-service lane for zero gain, while
   Catalog already publishes into Ordering twice over.)
2. **Project into Ordering** via a new `PromotionChanged` contract → `PromotionChangedConsumer` →
   `PromotionCopy`, exactly mirroring `OfferChanged`/`OfferChangedConsumer`/`OfferCopy`. **Migrations in BOTH**
   services.
3. **Fix the dead-code split by extraction, not duplication.** Create ONE pure domain component,
   `PromotionEvaluator` (`src/Services/Ordering/Domain/PromotionEvaluator.cs`), that owns eligibility,
   threshold measurement, reward computation, combinability selection and the **per-line discount
   allocation**. Both `PricingEngine.Price` and `CheckoutEndpoints.Checkout` call it. `PricingEngine` keeps its
   own tax/shipping seam; checkout keeps its own (richer) one. The rejected alternative — duplicating the
   promotion algorithm inside `CheckoutEndpoints` — is called out in NOTES.
4. **Extend `Promotion` additively** with `PromotionKind.Threshold = 9`, `MinimumAmountMinor`, `Combinable`,
   `GrantsFreeShipping`, `Scope`, `Currency`, `ActiveFrom`/`ActiveUntil`. Every existing kind and every
   existing `PricingTests` assertion keeps working.
5. **Widen the result shape** — `PricingResult.AppliedPromotionIds` (list) with `AppliedPromotionId` kept as a
   computed first-or-null so existing tests still compile; `CheckoutResponse` gains appended optional fields;
   `CheckoutAttempt` snapshots the applied promotion ids and per-line discounts (today
   `CheckoutAttemptLine.DiscountMinor` is hard-coded `0` at `CheckoutEndpoints.cs:375`).
6. **Shown == charged** — add `GET /cart/summary` to Ordering, which resolves offer prices + storefront
   discount + promotions with the **same evaluator** and returns the money preview. The storefront cart and
   checkout summary read it, so no promotion algorithm is ever re-implemented in TypeScript.
7. **Final pricing order** (see IMPLEMENTATION PLAN → "Pricing order contract") preserves
   `Net + Ship + Tax = Gross` and `trial balance = 0` with **no new ledger line** — the discount simply lowers
   the charged gross, exactly as PR #256 established.

## Feature Metadata

**Feature Type**: New Capability (with an embedded Refactor: de-duplicating the promotion math off the
dead `PricingEngine` path)
**Estimated Complexity**: **High** — 2 services, 3 migrations, a bus contract, a shared domain extraction on
the money path, an admin page, storefront display, and 4 test layers.
**Primary Systems Affected**: Catalog (Domain/Api/Infrastructure), Ordering (Domain/Api/Infrastructure),
BuildingBlocks.Contracts.Catalog, Admin (Blazor), Storefront (Next.js), integration tests, Playwright E2E,
docs (ADR/API/help), `scripts/e2e-verify.sh`.
**Dependencies**: none new. Existing: EF Core 10 + Npgsql, MassTransit (RabbitMQ), Blazor Server, Next.js 15 +
next-intl, xUnit, Testcontainers, Playwright.

---

## CONTEXT REFERENCES

### Relevant Codebase Files IMPORTANT: YOU MUST READ THESE FILES BEFORE IMPLEMENTING!

**The dormant engine you are extending**

- `src/Services/Ordering/Domain/Pricing.cs` (lines 10-20) — Why: `PromotionKind` enum you append
  `Threshold = 9` to. It already has `FreeShipping`, `QuantityTier`, `AutomaticProduct`,
  `AutomaticStorefront`, `CouponFixed/Percent`, `AutomaticCategory`, `BundleDiscount`.
- `src/Services/Ordering/Domain/Pricing.cs` (lines 44-80) — Why: the `Promotion` record + `Validate`. It has
  `AmountMinor`, `PercentOff`, `MinimumQuantity`, `ProductId`, `CategoryId`, `Active` — and **no amount
  threshold, no combinable flag, no currency, no window**. You add those additively.
- `src/Services/Ordering/Domain/Pricing.cs` (lines 82-90) — Why: `PricingResult` exposes a single
  `AppliedPromotionId`; stacking needs a collection. This ripples to `CheckoutResponse` + storefront display.
- `src/Services/Ordering/Domain/Pricing.cs` (lines 145-180) — Why: `PricingEngine.Price`. Lines 158-164 are
  the "best single wins" selection you generalise; lines 166-179 are the storefront-wide discount from
  PR #256 whose shape (`Math.Min(promo + storefront, subtotal)`) you must preserve.
- `src/Services/Ordering/Domain/Pricing.cs` (lines 218-241) — Why: `IsEligible` — the per-kind eligibility
  switch you add the `Threshold` case to.
- `src/Services/Ordering/Domain/Pricing.cs` (lines 243-278) — Why: `Evaluate` already computes an
  **eligible subtotal per scope** (product/category/bundle/tier lines vs whole subtotal). That is the exact
  hook for "a product-scoped promotion discounts only that product's lines".
- `src/Services/Ordering/Domain/Pricing.cs` (lines 92-143) — Why: `ITaxStrategy` / `HomeRegimeTaxStrategy`
  proportionally allocates the discount over the taxable subtotal (lines 128-142). Your per-line allocation
  makes this exact instead of approximate.

**The production money path (the one that actually charges)**

- `src/Services/Ordering/Api/Endpoints/CheckoutEndpoints.cs` (lines 29-38) — Why: the handler signature +
  DI you extend (you will need `db.PromotionCopies`; `TimeProvider time` is already injected).
- `src/Services/Ordering/Api/Endpoints/CheckoutEndpoints.cs` (lines 49-99) — Why: tenant/storefront
  resolution, `offerCopies` load, `approvedSupplierIds` gate (ADR-0048). Promotions load in the same place.
- `src/Services/Ordering/Api/Endpoints/CheckoutEndpoints.cs` (lines 101-129) — Why: **this is where the
  comparison base is produced** — `item.UnitPriceMinor` becomes the offer-resolved effective price. Your
  threshold and discount base is `Σ item.UnitPriceMinor × item.Quantity` computed AFTER this loop.
- `src/Services/Ordering/Api/Endpoints/CheckoutEndpoints.cs` (lines 139-180) — Why: `storefrontCopy` fetch,
  `subtotal`/`taxableSubtotal`/`allShippingCovered`, and the storefront-wide discount block (`:171-180`) your
  promotion discount is added to.
- `src/Services/Ordering/Api/Endpoints/CheckoutEndpoints.cs` (lines 182-241) — Why: the shipping gate
  (`anyShippable`), collect-at-warehouse, quote validation and `allShippingCovered` waiver. Free shipping from
  a promotion is applied **after** all of these and must not resurrect a rejected quote.
- `src/Services/Ordering/Api/Endpoints/CheckoutEndpoints.cs` (lines 243-275) — Why: the tax block. The
  proportional `taxableDiscountMinor` (`:262`) is what your per-line allocation replaces for exactness.
- `src/Services/Ordering/Api/Endpoints/CheckoutEndpoints.cs` (lines 330-383) — Why: the `CheckoutAttempt`
  snapshot. Note `DiscountMinor = 0` hard-coded on every line at `:375` — you fill it in.
- `src/Services/Ordering/Api/Endpoints/CheckoutEndpoints.cs` (line 464) — Why: `CheckoutResponse` — a
  positional record; new fields must be **appended with defaults**.

**The projection pattern to mirror (Offer → OfferCopy)**

- `src/Services/Catalog/Domain/Offer.cs` (lines 13-68) — Why: aggregate shape — `init` identity props,
  `private set` mutables, private ctor, static `Create`, XML docs on every public member.
- `src/Services/Catalog/Domain/Offer.cs` (lines 146-176) — Why: `SetStorefront`, `SetActiveWindow` (with the
  from-after-until guard) and `IsEffectiveAt` — copy these near-verbatim onto `Promotion`.
- `src/Services/Catalog/Api/Endpoints/OfferEndpoints.cs` (lines 21-30) — Why: the
  `MapGroup("/admin/…").RequireAuthorization(InternalClaimsAuth.AdminPolicy)` registration shape.
- `src/Services/Catalog/Api/Endpoints/OfferEndpoints.cs` (lines 79-129) — Why: **the Create canon** —
  duplicate-key guard → `BadRequest` (`:92-104`), `audit.RecordAsync(user.Mutation(...))` (`:115-116`), and
  the load-bearing comment at `:117-121`: **publish BEFORE `SaveChangesAsync`** or the outbox row is stranded.
- `src/Services/Catalog/Api/Endpoints/OfferEndpoints.cs` (lines 131-200) — Why: the Update canon — nullable
  request fields, `if (request.X is { } x)` partial-update style, `ApplyScope` opt-in so a partial update
  can't wipe scope/window.
- `src/Services/Catalog/Api/Endpoints/OfferEndpoints.cs` (lines 205-224) — Why: `ToEvent` / `DefaultTenantId`
  / `ToDto` helpers.
- `src/BuildingBlocks/Contracts/Catalog/OfferChanged.cs` (lines 12-39) — Why: the **append-only contract**
  canon — every new field is added at the end with a back-compatible default and a comment saying what an
  older copy means.
- `src/Services/Ordering/Domain/OfferCopy.cs` (lines 9-66) — Why: the read-copy shape, including
  `IsEffectiveFor(storefrontId, currency, now)` (`:52-57`) — copy this predicate onto `PromotionCopy`.
- `src/Services/Ordering/Infrastructure/Projections/OfferChangedConsumer.cs` (lines 9-39) — Why: the
  idempotent upsert consumer shape.
- `src/Services/Ordering/Api/Program.cs` (line 33) — Why: `bus.AddConsumer<OfferChangedConsumer>();` — where
  you register the new consumer.
- `src/Services/Catalog/Api/Program.cs` (lines 59-65) — Why: `app.MapOffers();` — where you register
  `MapPromotions()`.
- `src/Services/Catalog/Infrastructure/CatalogDbContext.cs` (line 24, lines 62-73) — Why: `DbSet<Offer>` +
  the `Entity<Offer>` config (string-converted enums with `HasMaxLength`, composite indexes).
- `src/Services/Ordering/Infrastructure/OrderingDbContext.cs` (line 24, lines 36-45, lines 86-100) — Why:
  `DbSet<OfferCopy>` + its config, and `StorefrontTaxCopy`'s config (a value-converted list, if you ever need
  one).
- `src/Services/Catalog/Infrastructure/Migrations/20260831100446_StorefrontDiscount.cs` (lines 1-30) — Why:
  the exact `AddColumn` migration shape, **schema-qualified** (`schema: "catalog"`), non-nullable with a
  `defaultValue`.

**Section 1 precedent — the storefront-wide discount slice to mirror end-to-end (commit `2010d24`)**

- `src/Services/Catalog/Domain/Storefront.cs` (`DiscountBasisPoints` ~lines 27-33, `SetDiscount` ~lines
  152-172, `DuplicateFrom` ~line 388) — Why: the "validated setter + null = leave as-is + carried on
  duplication" shape.
- `src/BuildingBlocks/Contracts/Catalog/StorefrontConfigChanged.cs` (the appended `int DiscountBps = 0`) —
  Why: appended-with-default contract evolution.
- `src/Services/Ordering/Domain/StorefrontTaxCopy.cs` (lines 16-21) — Why: the copy field + its doc comment.
- `src/Services/Ordering/Infrastructure/Projections/StorefrontConfigConsumer.cs` (~lines 25, 37) — Why: both
  the insert and the update branch must set the new field.
- `src/Services/Ordering/tests/PricingTests.cs` (lines 196-281) — Why: the six discount unit tests, including
  the explicit **`Net + Ship + Tax = Gross`** assertion at line 209. Mirror this style exactly.
- `tests/3commerce.IntegrationTests/MoneyFlowTests.cs` (lines 356-458) — Why: three end-to-end tests using a
  **distinct currency per test** to isolate the live copy, `WaitForDiscountCopyAsync` (line 460),
  `WaitForOfferPriceCopyAsync` (line 228), `fixture.ApproveSupplierAsync`, `SimulatePaymentAsync`,
  `WaitForStatusAsync`, and **`Assert.Equal(0, await fixture.TrialBalanceAsync())`**.
- `src/Admin/Components/Pages/CommerceOps.razor` (lines 55, 78, 88, 151, 356, 387, 421, 440, 539, 648,
  655-660) — Why: percent↔bps conversion (`PercentToBps`, `DiscountLabel`), the create form, the manage form,
  the table column, and the DTO with an **appended defaulted** parameter.
- `src/Admin/Resources/SharedResource.resx` (lines 815-816, 839) — Why: where `Ops.Field.Discount` /
  `Ops.Col.Discount` were added; you add `Promotions.*` keys in the same style — **and in all six locale
  files**.
- `src/Storefront/lib/gateway.ts` (lines 111-125, 145-175, 250-258) — Why: `StorefrontConfig` typing, the
  `raw`-cast + defaulting pattern for a back-compatible API field, and `CartDto`/`getCart`.
- `src/Storefront/app/cart/page.tsx` (lines 1-30, 44-66) — Why: `formatRate`, the conditional discount row,
  and the "items total" row.
- `src/Storefront/components/checkout/CheckoutForm.tsx` (lines 21-26, 38, 65-80, 110-120) — Why: prop
  threading, the local money math, and the `<Row>` summary lines.
- `src/Storefront/messages/en.json` (lines 135-140, 156-160) — Why: `cart.discount`, `cart.itemsTotal`,
  `checkout.discount` with an ICU `{percent}` argument — mirrored across **all six** locale files.
- `src/Storefront/e2e-admin/commerce-ops-discount.spec.ts` (lines 1-56) — Why: the admin spec canon — seed via
  the gateway API so the test owns its row, `loginAsAdmin`, **`press("Tab")` to commit a Blazor `@bind`**
  before clicking save, screenshots, then an API round-trip assertion.
- `src/Storefront/e2e/storefront-discount.spec.ts` (lines 1-60) — Why: the storefront spec canon — mutate
  config via the admin API, `expect.poll` the public config until the projection lands, shop in the browser,
  then **reset the demo store** so sibling specs see the baseline.

**Admin UI pattern**

- `src/Admin/Components/Pages/Offers.razor` (lines 1-4) — Why: `@page`, `@attribute [Authorize(Roles =
  "admin")]`, `@inject GatewayClient Gateway`, `@rendermode AdminRenderModes.InteractiveServerNoPrerender`.
- `src/Admin/Components/Pages/Offers.razor` (lines 51-58, 171-174) — Why: the modal overlay + close button +
  Save/Clear/Cancel button row.
- `src/Admin/Components/Pages/Offers.razor` (lines 138-156) — Why: **the storefront picker + `ActiveFrom` /
  `ActiveUntil` date inputs — copy these verbatim** for the promotion's scope fieldset.
- `src/Admin/Components/Pages/Offers.razor` (lines 212-232) — Why: `Save()` — including the
  **`AddDays(1).AddTicks(-1)`** "include the whole end day" trick (`:224`) and the
  "`LoadAsync` clears `_status`/`_error`, so set the confirmation AFTER the reload" rule (`:230`).
- `src/Admin/Components/Pages/Offers.razor` (lines 265-338) — Why: the `Safe*()` live-data dropdown helpers
  that swallow failures so the modal still opens (`SafeProducts`, `SafeSuppliers`, `SafeStorefronts`).
- `src/Admin/Components/Pages/Offers.razor` (lines 355-368) — Why: the `_form` class + private record DTOs at
  the bottom of `@code`.
- `src/Admin/Components/Layout/MainLayout.razor` (line 13) — Why: the `<NavLink href="/offers" …>` entry your
  `/promotions` entry sits beside.

**Cart / checkout display**

- `src/Services/Ordering/Api/Endpoints/CartEndpoints.cs` (lines 20, 51-57, 195-209) — Why: route group,
  `GetCart`, `ToResponse`, and the `CartResponse`/`CartItemResponse` records. **`GET /cart` returns the
  add-time catalog price, not the offer price** — that is precisely why `/cart/summary` must exist.

**Tests + tooling**

- `src/Services/Ordering/tests/PricingTests.cs` (lines 5-7, 291-308) — Why: `PricingEngine _engine = new();`
  and the `NewInput` / `Line` builders you extend with promotion parameters.
- `tests/3commerce.IntegrationTests/MoneyFlowTests.cs` (lines 28, 228-240, 460-475) — Why: `Checkout()` body
  builder and the `WaitFor…CopyAsync` polling helpers you clone for `WaitForPromotionCopyAsync`.
- `scripts/e2e-verify.sh` (lines 16-18) — Why: the `COVERAGE CHECKLIST` header that **must** be updated in
  the same change (AGENTS.md:240).
- `scripts/e2e-verify.sh` (lines 27-33) — Why: the existing A3 bullet already enumerates the Pricing engine's
  promotion coverage — extend that sentence, don't add a parallel one.
- `scripts/e2e-verify.sh` (lines 117-122) — Why: the L20 Playwright bullet listing the admin pages exercised.
- `AGENTS.md` (lines 209, 211, 230, 238, 240) — Why: the in-the-same-change doc/tracker/test-list rules.
- `AGENTS.md` (line 234, item 3) — Why: **every consumer class name must be globally unique across services**
  — the kebab queue name is derived from it.

### New Files to Create

- `src/Services/Catalog/Domain/Promotion.cs` — Catalog `Promotion` aggregate + `PromotionScope`,
  `PromotionRewardStatus` enums.
- `src/BuildingBlocks/Contracts/Catalog/PromotionChanged.cs` — Catalog → Ordering projection contract.
- `src/Services/Catalog/Api/Endpoints/PromotionEndpoints.cs` — `/admin/promotions` list/create/update.
- `src/Services/Catalog/Infrastructure/Migrations/<ts>_Promotions.cs` (+ `.Designer.cs`) — Catalog table.
- `src/Services/Catalog/tests/PromotionTests.cs` — Catalog domain invariants.
- `src/Services/Ordering/Domain/PromotionCopy.cs` — Ordering read copy + `IsEffectiveFor`.
- `src/Services/Ordering/Domain/PromotionEvaluator.cs` — **the shared evaluator** + `PromotionOutcome`.
- `src/Services/Ordering/Infrastructure/Projections/PromotionChangedConsumer.cs` — idempotent upsert.
- `src/Services/Ordering/Infrastructure/Migrations/<ts>_PromotionCopies.cs` (+ `.Designer.cs`).
- `src/Services/Ordering/Infrastructure/Migrations/<ts>_CheckoutAttemptPromotions.cs` (+ `.Designer.cs`).
- `src/Services/Ordering/tests/PromotionEvaluatorTests.cs` — evaluator unit tests.
- `tests/3commerce.IntegrationTests/PromotionProjectionTests.cs` — `PromotionChanged` → `PromotionCopy`.
- `src/Admin/Components/Pages/Promotions.razor` — admin CRUD page.
- `src/Storefront/e2e-admin/promotions-admin.spec.ts` — Playwright admin spec.
- `src/Storefront/e2e/storefront-promotions.spec.ts` — Playwright storefront spec.
- `docs/adr/0051-threshold-promotions-and-combinability.md` — ADR.

### Relevant Documentation YOU SHOULD READ THESE BEFORE IMPLEMENTING!

- `docs/adr/0047-storefront-scoped-active-window-offer-price.md`
  - Specific section: the whole ADR.
  - Why: defines "the offer-resolved effective selling price" — your comparison base — and the
    `shown == charged` rule this feature must not break.
- `docs/adr/0048-supplier-approval-gated-offer-availability.md`
  - Specific section: DECISION A (strict).
  - Why: an unapproved supplier's offer must never set a price — so it must never contribute to a threshold
    base either. `approvedSupplierIds` is already threaded through `CheckoutEndpoints.cs:84-94`.
- `docs/adr/0038-per-currency-shelf-prices-and-tax-entry.md`
  - Specific section: inclusive (AU GST / EU VAT) vs exclusive (US) semantics.
  - Why: your discount must shrink the taxable base in both regimes (see `PricingTests.cs:255-281`).
- `docs/adr/0050-per-country-ship-rules-and-ship-to-allowlist.md`
  - Specific section: `chargeDestinationTax` / `shippingCovered`.
  - Why: a line exempt from destination tax must not have its promotion discount counted into the tax base;
    a cart where shipping is already covered must not double-apply "free shipping".
- `docs/adr/0045-mandatory-per-storefront-ledger-attribution.md`
  - Specific section: per-storefront accounts.
  - Why: the promotion lowers the charged gross → posted revenue drops → **no new ledger line**, trial balance
    stays 0. Confirm this in `MoneyFlowTests`.
- `AGENTS.md` §Rules (lines 209-240) and §Definition of Done (lines 286-290)
  - Why: ADR + `adr_index.md`, `docs/api/api_contracts_index.md`, `docs/help/services.html`,
    `.ai-shared/plans/plan_status_executions.md`, and `scripts/e2e-verify.sh` + its COVERAGE CHECKLIST must
    all be updated **in the same change**.
- [EF Core — Migrations overview](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/)
  - Specific section: `dotnet ef migrations add` / `--project` + `--startup-project`.
  - Why: this repo splits Infrastructure (migrations) from Api (host); both flags are required.
- [MassTransit — Consumers & endpoint naming](https://masstransit.io/documentation/configuration/consumers)
  - Specific section: receive endpoint name conventions.
  - Why: grounds the "consumer class names must be globally unique across services" rule (AGENTS.md:234).
- [next-intl — Messages & ICU arguments](https://next-intl.dev/docs/usage/messages)
  - Specific section: interpolation.
  - Why: the promotion label uses `{name}` / `{percent}` arguments across six locale files.

### Patterns to Follow

**Naming Conventions**

- Catalog aggregate: `Promotion` (singular) in `ThreeCommerce.Catalog.Domain`; table `catalog."Promotions"`.
- Contract: `PromotionChanged` in `ThreeCommerce.BuildingBlocks.Contracts.Catalog` — a `record`, not `sealed`,
  matching `OfferChanged.cs:12`.
- Ordering read copy: `PromotionCopy` (mirrors `OfferCopy`), table `ordering."PromotionCopies"`.
- Consumer: `PromotionChangedConsumer` — **verify it is globally unique**:
  `grep -rn "class PromotionChangedConsumer" src/` must return exactly your new file.
- Domain rule violations throw `CatalogRuleException` in Catalog and `PricingRuleException` in Ordering
  (`Pricing.cs:281`).

**Error Handling**

Catalog endpoints wrap the domain call and translate the rule exception — `OfferEndpoints.cs:125-128`:

```csharp
catch (CatalogRuleException ex)
{
    return TypedResults.BadRequest(ex.Message);
}
```

Domain guards read like `Offer.SetActiveWindow` (`Offer.cs:154-164`):

```csharp
public void SetActiveWindow(DateTimeOffset? activeFrom, DateTimeOffset? activeUntil, DateTimeOffset now)
{
    if (activeFrom is { } from && activeUntil is { } until && from > until)
    {
        throw new CatalogRuleException("Offer active-from must not be after active-until.");
    }

    ActiveFrom = activeFrom;
    ActiveUntil = activeUntil;
    UpdatedAt = now;
}
```

**Outbox Pattern (load-bearing — this has bitten the repo before)**

`OfferEndpoints.cs:117-123`, verbatim comment included:

```csharp
// Publish BEFORE Save so the OfferChanged outbox row commits in the same transaction and
// is actually delivered. Publishing after SaveChanges strands it in the change tracker
// (never flushed) — Ordering's OfferCopy projection then never fires, so subscription/usage
// offers silently degrade to OneTime/Once at checkout (same outbox trap as the RMA/availability paths).
await publisher.Publish(ToEvent(offer, await ProductTypeAsync(db, offer.ProductId, ct)), ct);
await db.SaveChangesAsync(ct);
```

**Audit Pattern (every admin mutation)**

`OfferEndpoints.cs:115-116`:

```csharp
await audit.RecordAsync(user.Mutation(
    offer.TenantId, "Offer", offer.Id.ToString(), "catalog.offer.create", offer.ProductId.ToString()), ct);
```

**Append-only contract evolution**

`OfferChanged.cs:32-39`:

```csharp
    // Offer-as-price (ADR-0028): ... Appended, back-compatible defaults — a copy
    // from before these fields carries PriceMinor 0 (no offer price → checkout keeps the catalog price),
    // StorefrontId null (all storefronts), and an open-ended window.
    long PriceMinor = 0,
    Guid? StorefrontId = null,
    DateTimeOffset? ActiveFrom = null,
    DateTimeOffset? ActiveUntil = null);
```

**Effectiveness predicate on a read copy**

`OfferCopy.cs:52-57` — copy this shape onto `PromotionCopy` (adding the tenant/storefront match):

```csharp
public bool IsEffectiveFor(Guid storefrontId, string currency, DateTimeOffset now) =>
    Active
    && (StorefrontId is null || StorefrontId == storefrontId)
    && string.Equals(Currency, currency, StringComparison.OrdinalIgnoreCase)
    && (ActiveFrom is null || ActiveFrom <= now)
    && (ActiveUntil is null || now <= ActiveUntil);
```

**Idempotent projection consumer**

`OfferChangedConsumer.cs:11-38`:

```csharp
public async Task Consume(ConsumeContext<OfferChanged> context)
{
    var m = context.Message;
    var copy = await db.OfferCopies.SingleOrDefaultAsync(o => o.OfferId == m.OfferId, context.CancellationToken);
    if (copy is null)
    {
        copy = new OfferCopy { OfferId = m.OfferId };
        db.OfferCopies.Add(copy);
    }

    copy.TenantId = m.TenantId;
    // … every field assigned on BOTH the insert and update path …
    await db.SaveChangesAsync(context.CancellationToken);
}
```

**Money math (integer minor units only — no floats in the domain)**

`Pricing.cs:274-276` and the storefront-discount rounding at `Pricing.cs:171-174`:

```csharp
private static long LineTotal(PricingLineInput line) => checked(line.SellingPriceMinor * line.Quantity);
private static long Percent(long amount, int percent) => checked(amount * percent / 100);

var storefrontDiscount = input.StorefrontDiscountBps > 0
    ? (long)Math.Round(subtotal * input.StorefrontDiscountBps / 10000m, MidpointRounding.AwayFromZero)
    : 0;
var discountMinor = Math.Min(promotionDiscount + storefrontDiscount, subtotal);
```

**Money identity assertion (every money test)**

`PricingTests.cs:208-209`:

```csharp
// Net + Ship + Tax = Gross (Net = Subtotal − Discount).
Assert.Equal(result.GrossMinor, result.SubtotalMinor - result.DiscountMinor + result.ShippingMinor + result.TaxMinor);
```

**Admin percent↔bps conversion + column label**

`CommerceOps.razor:655-658`:

```csharp
private static string DiscountLabel(StorefrontDto s) => s.DiscountBasisPoints == 0 ? "—" : $"{s.DiscountBasisPoints / 100m:0.##}%";

// A percent entered in the UI (e.g. 8.25) → basis points (825), clamped to the domain's 0–100% range.
private static int PercentToBps(decimal percent) => (int)Math.Round(Math.Clamp(percent, 0m, 100m) * 100m, MidpointRounding.AwayFromZero);
```

**Admin date-window binding ("include the whole end day")**

`Offers.razor:222-224`:

```csharp
DateTimeOffset? activeFrom = _form.ActiveFrom is { } from ? new DateTimeOffset(from, TimeSpan.Zero) : null;
// Include the whole ActiveUntil day: an end date of the 5th means "effective through end of the 5th".
DateTimeOffset? activeUntil = _form.ActiveUntil is { } until ? new DateTimeOffset(until, TimeSpan.Zero).AddDays(1).AddTicks(-1) : null;
```

**Playwright: committing a Blazor `@bind` before saving**

`commerce-ops-discount.spec.ts:41-44`:

```ts
await discountField.fill("15");
// Blazor @bind commits on the change event — Tab blurs the field so the value reaches the circuit
// before the save click (a real user clicking away does the same).
await discountField.press("Tab");
await page.getByRole("button", { name: /save storefront settings/i }).first().click();
```

**Other Relevant Patterns**

- **Enums cross HTTP as numbers** (platform invariant, see `CommerceOps.razor:536-538`): admin `<select>`s bind
  to `int` fields and serialize numerically. Your promotion `scope`/`kind` dropdowns follow this.
- **Every admin label goes through `L["…"]`** and gets a `title="@L["….Tip"]"` tooltip; new keys land in all
  six `SharedResource*.resx` files.
- **Money entry tooltips must state the ADR-0038 tax semantics** (AGENTS.md:263) — the promotion's
  `MinimumAmountMinor` tooltip must say the threshold is measured on the item value **excluding tax and
  shipping**.
- **Test isolation via a distinct currency** — every `MoneyFlowTests` test picks an unused ISO code so its
  live `StorefrontTaxCopy` never wins tax resolution for another test's cart (`MoneyFlowTests.cs:359-361`).

---

## IMPLEMENTATION PLAN

### Pricing order contract (the normative specification — implement exactly this)

```
1.  per line: UnitPriceMinor = effective offer price (ResolvePricingOffer, approval-gated)
                             ?? current catalog price                    [already at CheckoutEndpoints.cs:101-129]
2.  subtotal        = Σ UnitPriceMinor × Quantity                        [the COMPARISON BASE — excl. tax/ship/fees]
3.  promotions      = PromotionEvaluator.Evaluate(lines, subtotal, shippingMinor, copies, now)
       3a. eligibility: tenant + storefront + currency + active + window + threshold met
       3b. per-promotion reward: discount on its own scope base, and/or free shipping
       3c. selection : better of [best single Exclusive] vs [Σ all Combinable]
                       benefit = discountMinor + (freeShipping ? shippingMinor : 0)
       3d. allocation: spread the won discount across the contributing lines (largest remainder)
4.  storefrontDiscount = round(subtotal × DiscountBasisPoints / 10000)   [PR #256 — always applied]
5.  discountMinor   = min(promotionDiscount + storefrontDiscount, subtotal)   [cap: goods never go negative]
6.  shippingMinor   = 0 if (freeShippingWon || !anyShippable || collectAtWarehouse || allShippingCovered)
                      else the validated selected/flat rate
7.  taxableDiscount = Σ line discount over destination-taxable lines only (exact, from step 3d + step 4's
                      proportional share) — replaces the approximate ratio at CheckoutEndpoints.cs:262
8.  taxBase         = taxableSubtotal − taxableDiscount + taxableShipping
    tax             = inclusive ? round(taxBase × bps / (10000+bps)) : round(taxBase × bps / 10000)
9.  chargeBase      = subtotal − discountMinor + shippingMinor
    net             = inclusive ? chargeBase : chargeBase + tax
10. INVARIANT: Net + Ship + Tax = Gross  AND  trial balance = 0 (no new ledger line — the lower charged
    gross simply posts less revenue, exactly as PR #256 established).
```

**Why this order.** The promotion must be measured and applied on the *offer-resolved* base (step 1→2), or a
storefront running an offer would compute thresholds against a price the shopper never sees. Promotions are
merchandising rules attached to specific carts; the storefront-wide discount is a blanket store setting, so it
applies **after** and stacks additively — the identical shape `Pricing.cs:170-174` already uses. Both are
capped together at the subtotal so goods can never go negative. Tax is last on the discounted base so both
ADR-0038 regimes stay correct. Free shipping only ever *zeroes* an already-computed shipping charge; it never
resurrects a rate that the quote guards rejected.

### Phase 1: Foundation — Catalog owns the Promotion

Create the aggregate, its persistence, its admin API and its unit tests. Nothing in Ordering changes yet, so
the whole phase is independently shippable and cannot break the money path.

**Tasks:** 1–9

- ADR + index (the architectural decision must be recorded before the code lands — AGENTS.md:209)
- `Promotion` aggregate with validated setters
- `CatalogDbContext` registration + migration
- `PromotionChanged` contract
- `/admin/promotions` endpoints (list/create/update) with the duplicate guard, audit, publish-before-save
- Catalog domain tests

### Phase 2: Core Implementation — project into Ordering

Stand up the read copy and the consumer so checkout *can* see promotions, still without changing any charge.

**Tasks:** 10–15

- `PromotionCopy` + `IsEffectiveFor`
- `OrderingDbContext` registration + migration
- `PromotionChangedConsumer` + registration
- projection integration test

### Phase 3: The shared evaluator (resolves the dead-code split)

**This is the phase that makes the feature correct.** Extract the single algorithm both money paths call.

**Tasks:** 16–19

- `PromotionEvaluator` — pure, no EF, no HTTP: eligibility → thresholds → rewards → combinability selection →
  per-line allocation.
- `Pricing.cs` — add `PromotionKind.Threshold`, the new `Promotion` fields, `PricingResult.AppliedPromotionIds`,
  and **delete the inline selection at `Pricing.cs:158-164`** in favour of a call into the evaluator.
- New evaluator unit tests + extended `PricingTests`.

### Phase 4: Integration — checkout charges it

**Tasks:** 20–23

- `CheckoutAttempt` / `CheckoutAttemptLine` promotion snapshot columns + migration.
- `CheckoutEndpoints` calls the evaluator, applies free shipping, allocates the taxable discount exactly, and
  appends the new `CheckoutResponse` fields.
- `MoneyFlowTests` end-to-end with trial-balance assertions.

### Phase 5: Integration — shown == charged on the storefront

**Tasks:** 24–28

- `GET /cart/summary` in Ordering (offer prices + storefront discount + promotions, one evaluator).
- Storefront gateway client, cart page, checkout summary, six locale files.

### Phase 6: Integration — admin surface

**Tasks:** 29–31

- `Promotions.razor` + nav entry + six resx files.

### Phase 7: Testing, docs & regression

**Tasks:** 32–38

- Two Playwright specs, `e2e-verify.sh` + its COVERAGE CHECKLIST, API/help docs, the status tracker, and the
  full regression run **including re-running the existing admin specs**.

---

## STEP-BY-STEP TASKS

IMPORTANT: Execute every task in order, top to bottom. Each task is atomic and independently testable.

### Task Format Guidelines

Use information-dense keywords for clarity:

- **CREATE**: New files or components
- **UPDATE**: Modify existing files
- **ADD**: Insert new functionality into existing code
- **REMOVE**: Delete deprecated code
- **REFACTOR**: Restructure without changing behavior
- **MIRROR**: Copy pattern from elsewhere in codebase

---

### 1. CREATE `docs/adr/0051-threshold-promotions-and-combinability.md`

- **IMPLEMENT**: The ADR for this feature. Sections: Context (the dormant engine at `Pricing.cs:10-279`;
  `PricingEngine` is off the production path; ADR-0008 forbids checkout querying Catalog), Decision
  (Catalog-owned `Promotion` projected to Ordering; `PromotionKind.Threshold`; scope = Storefront|Product;
  base = offer-resolved effective selling price × qty excluding tax/ship/fees; rewards = free shipping and/or
  percent-or-fixed discount; combinability = better of best-exclusive vs sum-of-combinables by customer
  benefit; the **normative pricing order** copied verbatim from this plan's "Pricing order contract"; the
  shared `PromotionEvaluator` extraction), Consequences (trial balance unaffected — no new ledger line; the
  storefront-wide discount of PR #256 remains a separate, always-applied setting; `CheckoutResponse` and
  `PricingResult` grow a promotion-id collection).
- **PATTERN**: `docs/adr/0047-storefront-scoped-active-window-offer-price.md` (whole file) —
  Context/Decision/Consequences with explicit cross-references to the ADRs it extends.
- **IMPORTS**: n/a (markdown).
- **GOTCHA**: State explicitly that both thresholds set = **AND**. Also state that a promotion's
  `MinimumAmountMinor` and fixed `AmountMinor` are **denominated in the promotion's `Currency`** and never
  converted (the repo has a strict no-FX posture — see ADR-0041).
- **VALIDATE**: `test -f docs/adr/0051-threshold-promotions-and-combinability.md && grep -q "Combinable" docs/adr/0051-threshold-promotions-and-combinability.md && echo OK`

### 2. UPDATE `docs/adr/adr_index.md`

- **IMPLEMENT**: Append a `| [0051](./0051-threshold-promotions-and-combinability.md) | <one-line summary> |
  Accepted | Catalog / Ordering / pricing |` row.
- **PATTERN**: `docs/adr/adr_index.md` (the ADR-0050 row, last line) — same four columns, same density.
- **IMPORTS**: n/a.
- **GOTCHA**: The table rows are long single lines; do not wrap them.
- **VALIDATE**: `grep -c "0051-threshold-promotions" docs/adr/adr_index.md`

### 3. CREATE `src/Services/Catalog/Domain/Promotion.cs`

- **IMPLEMENT**:
  ```
  public enum PromotionScope { Storefront = 1, Product = 2 }
  public enum PromotionStatus { Active = 1, Inactive = 2 }

  public sealed class Promotion
  {
      Guid Id { get; init; }                       // Guid.CreateVersion7()
      Guid TenantId { get; init; }
      Guid? StorefrontId { get; private set; }     // null = every storefront of Currency
      required string Name { get; set; }           // shown in cart/checkout + admin (max 120)
      required string Currency { get; init; }      // ISO-4217; thresholds + fixed amounts are in THIS currency
      PromotionScope Scope { get; private set; }
      Guid? ProductId { get; private set; }        // required iff Scope == Product; must be null otherwise
      long MinimumAmountMinor { get; private set; }// 0 = no money threshold
      int MinimumQuantity { get; private set; }    // 0 = no quantity threshold  (both set ⇒ AND)
      bool GrantsFreeShipping { get; private set; }
      int PercentOff { get; private set; }         // 0–100; mutually exclusive with DiscountAmountMinor
      long DiscountAmountMinor { get; private set; }
      bool Combinable { get; private set; }        // false = Exclusive
      DateTimeOffset? ActiveFrom / ActiveUntil { get; private set; }
      PromotionStatus Status { get; private set; } = Active;
      DateTimeOffset CreatedAt { get; init; } / UpdatedAt { get; private set; }
      bool IsActive => Status == PromotionStatus.Active;
  }
  ```
  Methods: `static Create(tenantId, name, currency, scope, productId, now)`;
  `SetThreshold(long minimumAmountMinor, int minimumQuantity, DateTimeOffset now)`;
  `SetReward(bool grantsFreeShipping, int percentOff, long discountAmountMinor, DateTimeOffset now)`;
  `SetCombinable(bool combinable, DateTimeOffset now)`; `SetStorefront(Guid? storefrontId, …)`;
  `SetActiveWindow(DateTimeOffset? from, DateTimeOffset? until, …)`;
  `IsEffectiveAt(DateTimeOffset now, Guid storefrontId)`; `Activate` / `Deactivate`.
  Invariants (all throw `CatalogRuleException`): tenant/name/currency required; currency length 3;
  `Scope == Product` ⟺ `ProductId is not null`; `MinimumAmountMinor >= 0`; `MinimumQuantity >= 0`; **at least
  one threshold set** (`MinimumAmountMinor > 0 || MinimumQuantity > 0`); `PercentOff` in `[0,100]`;
  `DiscountAmountMinor >= 0`; **not both** `PercentOff > 0` and `DiscountAmountMinor > 0`; **at least one
  reward** (`GrantsFreeShipping || PercentOff > 0 || DiscountAmountMinor > 0`); `from <= until`.
- **PATTERN**: `src/Services/Catalog/Domain/Offer.cs:13-68` (shape), `:70-116` (`Create` guards), `:146-176`
  (`SetStorefront` / `SetActiveWindow` / `IsEffectiveAt` — copy near-verbatim), `:245-255`
  (`Activate`/`Deactivate`); `src/Services/Catalog/Domain/Storefront.cs` `SetDiscount` (~line 152) for the
  range-validated setter shape.
- **IMPORTS**: `namespace ThreeCommerce.Catalog.Domain;` — `CatalogRuleException` is already in this
  namespace (used by `Offer.cs:77`); no `using` needed.
- **GOTCHA**: Money is **integer minor units** — never `decimal`/`double` on the aggregate.
  `Currency.ToUpperInvariant()` on `Create` (mirrors `Offer.cs:111`). Give **every public member an XML doc
  comment**: the build runs analyzers as errors and this repo documents every domain member.
- **VALIDATE**: `dotnet build src/Services/Catalog/Domain/3commerce.Catalog.Domain.csproj -warnaserror`

### 4. UPDATE `src/Services/Catalog/Infrastructure/CatalogDbContext.cs`

- **IMPLEMENT**: Add `public DbSet<Promotion> Promotions => Set<Promotion>();` beside `DbSet<Offer>`, and a
  `modelBuilder.Entity<Promotion>(promotion => { … })` block: `Scope` and `Status` converted
  `HasConversion<string>().HasMaxLength(16)`; `Currency` `HasMaxLength(3)`; `Name` `HasMaxLength(120)`;
  indexes `new { TenantId, StorefrontId }` and `new { TenantId, ProductId }`.
- **PATTERN**: `src/Services/Catalog/Infrastructure/CatalogDbContext.cs:24` (DbSet) and `:62-73`
  (`Entity<Offer>` config — string-converted enums with explicit `HasMaxLength`, composite indexes).
- **IMPORTS**: none new (`ThreeCommerce.Catalog.Domain` is already imported).
- **GOTCHA**: `modelBuilder.HasDefaultSchema("catalog")` at `:36` means the table lands in the `catalog`
  schema automatically — do **not** hand-qualify it (ADR-0022).
- **VALIDATE**: `dotnet build src/Services/Catalog/Infrastructure/3commerce.Catalog.Infrastructure.csproj -warnaserror`

### 5. CREATE the Catalog `Promotions` migration

- **IMPLEMENT**: Run
  ```bash
  dotnet ef migrations add Promotions \
    --project src/Services/Catalog/Infrastructure/3commerce.Catalog.Infrastructure.csproj \
    --startup-project src/Services/Catalog/Api/3commerce.Catalog.Api.csproj
  ```
  then **immediately** `dotnet format src/Services/Catalog/Infrastructure/3commerce.Catalog.Infrastructure.csproj`.
  Review the generated `CreateTable` — it must carry `schema: "catalog"`.
- **PATTERN**: `src/Services/Catalog/Infrastructure/Migrations/20260831100446_StorefrontDiscount.cs:1-30`
  (schema-qualified, `#nullable disable`, `/// <inheritdoc />` on the class and both methods).
- **IMPORTS**: generated.
- **GOTCHA**: **`dotnet format` the Infrastructure csproj after EVERY `ef migrations add`** — the generated
  `.Designer.cs` and the model snapshot violate the repo's analyzer rules (IDE0040 et al.) and CI's
  `dotnet format --verify-no-changes` gate fails otherwise. Also confirm the model snapshot
  (`CatalogDbContextModelSnapshot.cs`) was regenerated.
- **VALIDATE**: `dotnet ef migrations has-pending-model-changes --project src/Services/Catalog/Infrastructure/3commerce.Catalog.Infrastructure.csproj --startup-project src/Services/Catalog/Api/3commerce.Catalog.Api.csproj && dotnet format src/Services/Catalog/Infrastructure/3commerce.Catalog.Infrastructure.csproj --verify-no-changes`

### 6. CREATE `src/BuildingBlocks/Contracts/Catalog/PromotionChanged.cs`

- **IMPLEMENT**:
  ```csharp
  public record PromotionChanged(
      Guid PromotionId, Guid TenantId, Guid? StorefrontId, string Name, string Currency,
      PromotionScopeKind Scope, Guid? ProductId,
      long MinimumAmountMinor, int MinimumQuantity,
      bool GrantsFreeShipping, int PercentOff, long DiscountAmountMinor,
      bool Combinable, bool Active,
      DateTimeOffset? ActiveFrom = null, DateTimeOffset? ActiveUntil = null);
  ```
  plus `public enum PromotionScopeKind { Storefront = 1, Product = 2 }` in the same contracts assembly
  (BuildingBlocks is the only type both services may share).
- **PATTERN**: `src/BuildingBlocks/Contracts/Catalog/OfferChanged.cs:1-39` — a plain `record`, a `<summary>`
  explaining why the projection exists (cite ADR-0008), and per-field comments.
- **IMPORTS**: `namespace ThreeCommerce.BuildingBlocks.Contracts.Catalog;`
- **GOTCHA**: Contracts are **append-only** — never reorder or remove a positional parameter, and every future
  field must arrive at the end with a back-compatible default (`OfferChanged.cs:24-39` is the canon). Do not
  reference `ThreeCommerce.Catalog.Domain.PromotionScope` from the contract; the enum must live in
  BuildingBlocks and Catalog maps to it.
- **VALIDATE**: `dotnet build src/BuildingBlocks/3commerce.BuildingBlocks.csproj -warnaserror`

### 7. CREATE `src/Services/Catalog/Api/Endpoints/PromotionEndpoints.cs`

- **IMPLEMENT**: `public static class PromotionEndpoints` with
  `MapPromotions(this IEndpointRouteBuilder app)` →
  `app.MapGroup("/admin/promotions").WithTags("Promotions").RequireAuthorization(InternalClaimsAuth.AdminPolicy)`
  and `MapGet("/", List)`, `MapPost("/", Create)`, `MapPut("/{id:guid}", Update)`.
  - `List(Guid? tenantId, Guid? storefrontId, CatalogDbContext db, IConfiguration config, CancellationToken ct)`
    → filter by `tenantId ?? DefaultTenantId(config)`, optional storefront, `OrderBy(p => p.Name).ThenBy(p => p.Id)`;
    resolve product titles in one `ToDictionaryAsync` for the DTO (mirrors `OfferEndpoints.cs:67-72`).
  - `Create(CreatePromotionRequest request, …, IPublishEndpoint publisher, IAuditRecorder audit, ClaimsPrincipal user, TimeProvider clock, …)`
    → duplicate guard on `(TenantId, StorefrontId, Name)` → `BadRequest`; `Promotion.Create` + `SetThreshold`
    + `SetReward` + `SetCombinable` + `SetStorefront` + `SetActiveWindow`; `db.Promotions.Add`;
    `audit.RecordAsync(user.Mutation(tenantId, "Promotion", id, "catalog.promotion.create", …))`;
    **`await publisher.Publish(ToEvent(promotion), ct)` BEFORE `await db.SaveChangesAsync(ct)`**;
    `TypedResults.Created($"/admin/promotions/{id}", ToDto(promotion))`.
  - `Update(Guid id, UpdatePromotionRequest request, …)` → `NotFound` when missing; nullable-field partial
    update (`if (request.PercentOff is { } p)` style); an `ApplyScope` flag guarding
    `SetStorefront`/`SetActiveWindow`; `Active` toggles `Activate`/`Deactivate`; audit; publish-before-save.
  - Records: `CreatePromotionRequest`, `UpdatePromotionRequest`, `PromotionDto` — with
    `[property: Required]`, `[property: Range(0, 100)] int PercentOff`, `[property: Range(0, long.MaxValue)]`
    on money, `[property: StringLength(3, MinimumLength = 3)] string Currency`.
- **PATTERN**: `src/Services/Catalog/Api/Endpoints/OfferEndpoints.cs:21-30` (group), `:79-129` (Create incl.
  duplicate guard `:92-104`, audit `:115-116`, publish-before-save `:117-123`), `:131-200` (Update partial
  style + `ApplyScope` `:183-187`), `:205-224` (`ToEvent`/`DefaultTenantId`/`ToDto`), `:227-263` (request and
  response records).
- **IMPORTS**:
  ```csharp
  using System.ComponentModel.DataAnnotations;
  using System.Security.Claims;
  using MassTransit;
  using Microsoft.AspNetCore.Http.HttpResults;
  using Microsoft.EntityFrameworkCore;
  using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
  using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;
  using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
  using ThreeCommerce.Catalog.Domain;
  using ThreeCommerce.Catalog.Infrastructure;
  ```
- **GOTCHA**: (a) **Publish before Save** — see the verbatim comment in Patterns; publishing after Save
  strands the outbox row and Ordering's copy never updates. (b) The gateway routes `/api/catalog/{**catch-all}`
  (`src/Gateway/appsettings.json:23-30`) so **no gateway change is needed**. (c) A `Guid?` minimal-API query
  parameter 400s on free text — if you ever add a free-text filter, follow the id-or-title `string?` pattern
  at `OfferEndpoints.cs:42-56`.
- **VALIDATE**: `dotnet build src/Services/Catalog/Api/3commerce.Catalog.Api.csproj -warnaserror`

### 8. UPDATE `src/Services/Catalog/Api/Program.cs`

- **IMPLEMENT**: Add `app.MapPromotions();` immediately after `app.MapOffers();`.
- **PATTERN**: `src/Services/Catalog/Api/Program.cs:59-65`.
- **IMPORTS**: none (the endpoints class is in the already-imported `ThreeCommerce.Catalog.Api.Endpoints`).
- **GOTCHA**: Keep the ordering next to `MapOffers` so the OpenAPI tag grouping stays readable.
- **VALIDATE**: `dotnet build src/Services/Catalog/Api/3commerce.Catalog.Api.csproj -warnaserror`

### 9. CREATE `src/Services/Catalog/tests/PromotionTests.cs`

- **IMPLEMENT**: xUnit facts covering: create with a money threshold + free shipping; create with a quantity
  threshold + percent; **both thresholds set are AND**ed at the domain level (the flag is stored, evaluation
  is Ordering's job — assert the persisted values); rejects no-threshold; rejects no-reward; rejects both
  `PercentOff` and `DiscountAmountMinor`; rejects `PercentOff > 100`; rejects `Scope == Product` with null
  `ProductId` and `Scope == Storefront` with a `ProductId`; rejects `from > until`;
  `IsEffectiveAt` true inside the window / false outside / false when `Inactive` / false for another
  storefront; `Deactivate`/`Activate` round-trip.
- **PATTERN**: `src/Services/Catalog/tests/StorefrontDiscountTests.cs` (whole file — the PR #256 domain test
  added at the same layer) and `src/Services/Catalog/tests/OfferTests.cs`.
- **IMPORTS**: `using ThreeCommerce.Catalog.Domain;` — `namespace ThreeCommerce.Catalog.Tests;`
- **GOTCHA**: Assert on the exception **message substring** with
  `Assert.Contains("…", ex.Message, StringComparison.Ordinal)` — the repo's analyzers require the explicit
  comparison overload (`PricingTests.cs:288`).
- **VALIDATE**: `dotnet test src/Services/Catalog/tests/3commerce.Catalog.Tests.csproj --filter FullyQualifiedName~PromotionTests`

### 10. CREATE `src/Services/Ordering/Domain/PromotionCopy.cs`

- **IMPLEMENT**: `public class PromotionCopy` with `Guid PromotionId { get; init; }` as the key and settable
  mirrors of every `PromotionChanged` field, plus:
  ```csharp
  /// <summary>Whether this promotion can apply for <paramref name="storefrontId"/> in
  /// <paramref name="currency"/> at <paramref name="now"/>: Active, tenant-matching, storefront-matching
  /// (or all-storefront), currency-matching, and inside the [ActiveFrom, ActiveUntil] window.</summary>
  public bool IsEffectiveFor(Guid tenantId, Guid storefrontId, string currency, DateTimeOffset now) =>
      Active
      && TenantId == tenantId
      && (StorefrontId is null || StorefrontId == storefrontId)
      && string.Equals(Currency, currency, StringComparison.OrdinalIgnoreCase)
      && (ActiveFrom is null || ActiveFrom <= now)
      && (ActiveUntil is null || now <= ActiveUntil);
  ```
- **PATTERN**: `src/Services/Ordering/Domain/OfferCopy.cs:9-66` — settable properties (the consumer writes
  them), XML docs stating what a legacy/default value means, and `IsEffectiveFor` at `:52-57`.
- **IMPORTS**: `using ThreeCommerce.BuildingBlocks.Contracts.Catalog;` (for `PromotionScopeKind`);
  `namespace ThreeCommerce.Ordering.Domain;`
- **GOTCHA**: `Currency` must default to `""` (like `OfferCopy.cs:33`) so a legacy row can never accidentally
  match a cart currency. The currency guard is the **no-FX** invariant: a promotion in EUR must never apply to
  an AUD cart.
- **VALIDATE**: `dotnet build src/Services/Ordering/Domain/3commerce.Ordering.Domain.csproj -warnaserror`

### 11. UPDATE `src/Services/Ordering/Infrastructure/OrderingDbContext.cs`

- **IMPLEMENT**: `public DbSet<PromotionCopy> PromotionCopies => Set<PromotionCopy>();` beside
  `DbSet<OfferCopy>`, and `modelBuilder.Entity<PromotionCopy>(promotion => { promotion.HasKey(p =>
  p.PromotionId); promotion.Property(p => p.Currency).HasMaxLength(3); promotion.Property(p =>
  p.Name).HasMaxLength(120); promotion.Property(p => p.Scope).HasConversion<string>().HasMaxLength(16);
  promotion.HasIndex(p => new { p.TenantId, p.StorefrontId, p.Active }); });`
- **PATTERN**: `src/Services/Ordering/Infrastructure/OrderingDbContext.cs:24` and `:36-45`
  (`Entity<OfferCopy>` — explicit `HasKey` on the projected id, `HasMaxLength(3)` on currency, a covering
  index for the checkout query).
- **IMPORTS**: none new.
- **GOTCHA**: Index on `(TenantId, StorefrontId, Active)` matters — checkout loads promotions on **every**
  checkout and every `/cart/summary` call.
- **VALIDATE**: `dotnet build src/Services/Ordering/Infrastructure/3commerce.Ordering.Infrastructure.csproj -warnaserror`

### 12. CREATE `src/Services/Ordering/Infrastructure/Projections/PromotionChangedConsumer.cs`

- **IMPLEMENT**: `public sealed class PromotionChangedConsumer(OrderingDbContext db) :
  IConsumer<PromotionChanged>` — find-or-add by `PromotionId`, then assign **every** field on both paths, then
  `SaveChangesAsync(context.CancellationToken)`.
- **PATTERN**: `src/Services/Ordering/Infrastructure/Projections/OfferChangedConsumer.cs:9-39` (verbatim
  shape); `StorefrontConfigConsumer.cs:~25,~37` for the "set the new field on **both** the insert and the
  update branch" trap.
- **IMPORTS**:
  ```csharp
  using MassTransit;
  using Microsoft.EntityFrameworkCore;
  using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
  using ThreeCommerce.Ordering.Domain;
  ```
- **GOTCHA**: **Consumer class names must be globally unique across services** (AGENTS.md:234 item 3) — the
  kebab-case queue name is derived from the class name, and two same-named consumers in different services
  silently compete on one queue. Verify with
  `grep -rn "class PromotionChangedConsumer" src/ | wc -l` → must be `1`.
- **VALIDATE**: `dotnet build src/Services/Ordering/Infrastructure/3commerce.Ordering.Infrastructure.csproj -warnaserror && test "$(grep -rl 'class PromotionChangedConsumer' src/ | wc -l | tr -d ' ')" = 1`

### 13. UPDATE `src/Services/Ordering/Api/Program.cs`

- **IMPLEMENT**: Add `bus.AddConsumer<PromotionChangedConsumer>();` immediately after
  `bus.AddConsumer<OfferChangedConsumer>();`.
- **PATTERN**: `src/Services/Ordering/Api/Program.cs:33`.
- **IMPORTS**: none (the projections namespace is already imported for `OfferChangedConsumer`).
- **GOTCHA**: Registration is what creates the receive endpoint; forget it and the projection silently never
  runs — the exact failure mode the integration test in task 15 catches.
- **VALIDATE**: `dotnet build src/Services/Ordering/Api/3commerce.Ordering.Api.csproj -warnaserror`

### 14. CREATE the Ordering `PromotionCopies` migration

- **IMPLEMENT**:
  ```bash
  dotnet ef migrations add PromotionCopies \
    --project src/Services/Ordering/Infrastructure/3commerce.Ordering.Infrastructure.csproj \
    --startup-project src/Services/Ordering/Api/3commerce.Ordering.Api.csproj
  dotnet format src/Services/Ordering/Infrastructure/3commerce.Ordering.Infrastructure.csproj
  ```
- **PATTERN**: `src/Services/Ordering/Infrastructure/Migrations/20260831100512_StorefrontDiscountCopy.cs`
  (the PR #256 Ordering-side migration).
- **IMPORTS**: generated.
- **GOTCHA**: `dotnet format` the Infrastructure csproj **immediately** (see task 5). Confirm the generated
  `CreateTable` carries `schema: "ordering"` and that `OrderingDbContextModelSnapshot.cs` was regenerated.
- **VALIDATE**: `dotnet ef migrations has-pending-model-changes --project src/Services/Ordering/Infrastructure/3commerce.Ordering.Infrastructure.csproj --startup-project src/Services/Ordering/Api/3commerce.Ordering.Api.csproj && dotnet format src/Services/Ordering/Infrastructure/3commerce.Ordering.Infrastructure.csproj --verify-no-changes`

### 15. CREATE `tests/3commerce.IntegrationTests/PromotionProjectionTests.cs`

- **IMPLEMENT**: Publish a `PromotionChanged` through the fixture, poll until a `PromotionCopy` row exists with
  the expected fields; publish an update for the same `PromotionId` and assert the row is **updated, not
  duplicated** (idempotent upsert); publish with `Active: false` and assert `Active == false`.
- **PATTERN**: `tests/3commerce.IntegrationTests/StorefrontTaxProjectionTests.cs` (whole file) — the same
  publish-then-poll shape PR #256 extended; `MoneyFlowTests.cs:460-475` (`WaitForDiscountCopyAsync`) for the
  polling helper.
- **IMPORTS**: `using ThreeCommerce.BuildingBlocks.Contracts.Catalog;` plus the fixture's usings.
- **GOTCHA**: The projection is asynchronous — **always poll with a timeout**, never assert immediately.
  Testcontainers needs Docker running. Cap the factory's Npgsql `MaxPoolSize` if you add a new factory (this
  repo has flaked on `53300 too many clients`); reuse the existing fixture instead.
- **VALIDATE**: `dotnet test tests/3commerce.IntegrationTests/3commerce.IntegrationTests.csproj --filter FullyQualifiedName~PromotionProjectionTests`

### 16. CREATE `src/Services/Ordering/Domain/PromotionEvaluator.cs`

- **IMPLEMENT**: A pure static class — **no EF, no HTTP, no `DateTime.UtcNow`** (time is a parameter):
  ```csharp
  /// <summary>One cart line as the promotion engine sees it: the OFFER-RESOLVED effective selling price
  /// (excl. tax/shipping/fees) and quantity. Index-stable — the outcome's per-line allocation is parallel.</summary>
  public readonly record struct PromotionLine(Guid ProductId, long UnitPriceMinor, int Quantity)
  {
      public long TotalMinor => checked(UnitPriceMinor * Quantity);
  }

  /// <summary>What the engine decided. LineDiscountsMinor is parallel to the input lines and sums EXACTLY
  /// to DiscountMinor (largest-remainder allocation), so callers can compute a tax base per line.</summary>
  public sealed record PromotionOutcome(
      long DiscountMinor,
      bool FreeShippingApplied,
      IReadOnlyList<Guid> AppliedPromotionIds,
      IReadOnlyList<long> LineDiscountsMinor)
  {
      public static PromotionOutcome None(int lineCount) => new(0, false, [], new long[lineCount]);
  }

  public static class PromotionEvaluator
  {
      public static PromotionOutcome Evaluate(
          IReadOnlyList<PromotionLine> lines,
          IReadOnlyList<PromotionCopy> promotions,
          Guid tenantId, Guid storefrontId, string currency,
          long shippingMinor, DateTimeOffset now);
  }
  ```
  Algorithm, in order:
  1. `subtotal = lines.Sum(l => l.TotalMinor)`; if `subtotal <= 0` or `promotions.Count == 0` →
     `PromotionOutcome.None(lines.Count)`.
  2. **Eligible** = `p.IsEffectiveFor(tenantId, storefrontId, currency, now)` AND the threshold is met on its
     base:
     - `Scope == Storefront` → `baseAmount = subtotal`, `baseQuantity = Σ Quantity` (whole cart).
     - `Scope == Product` → `baseAmount`/`baseQuantity` summed over `lines.Where(l => l.ProductId == p.ProductId)`;
       if no line matches, ineligible.
     - met ⟺ `(p.MinimumAmountMinor == 0 || baseAmount >= p.MinimumAmountMinor)` **AND**
       `(p.MinimumQuantity == 0 || baseQuantity >= p.MinimumQuantity)`.
  3. **Reward** per eligible promotion: `discountBase` = the same scope base used in step 2;
     `raw = p.PercentOff > 0 ? checked(discountBase * p.PercentOff / 100) : p.DiscountAmountMinor`;
     `discount = Math.Clamp(raw, 0, discountBase)` (a fixed amount can never exceed its own scope base).
  4. **Selection**: `benefit(d, fs) = d + (fs ? shippingMinor : 0)`.
     - `bestExclusive` = the eligible non-combinable candidate with the greatest `benefit`, ties broken by
       ascending `PromotionId` (deterministic, mirrors `Pricing.cs:163`).
     - `combined` = all eligible combinable candidates: `discount = Math.Min(Σ discounts, subtotal)`,
       `freeShipping = any GrantsFreeShipping`, ids ordered ascending.
     - winner = the greater `benefit`; **tie → `combined`** (more promotions shown is the customer-visible,
       deterministic choice); if `combined` is empty, `bestExclusive`; if both empty → `None`.
  5. **Allocation**: for each winning promotion, spread its discount across its own contributing line indexes
     in proportion to each line's `TotalMinor`, using **largest-remainder** so the parts sum exactly. Sum the
     per-promotion allocations; then clamp the final vector so no line's allocation exceeds that line's
     `TotalMinor` and the vector sums to `DiscountMinor` (the storefront-wide discount is NOT included here —
     the caller adds it).
- **PATTERN**: `src/Services/Ordering/Domain/Pricing.cs:243-266` (`Evaluate` — the per-scope eligible-subtotal
  idea), `:158-164` (the best-single ordering + `ThenBy(id)` tiebreak you generalise), `:274-276`
  (`checked` integer helpers). `src/Services/Ordering/Domain/OfferCopy.cs:75-116` for the "pure static
  resolution class" shape.
- **IMPORTS**: `namespace ThreeCommerce.Ordering.Domain;` — no `using` beyond the implicit ones;
  `PromotionCopy` and `PricingRuleException` are in the same namespace.
- **GOTCHA**: (a) All arithmetic in `long` with `checked` — no `double`. (b) Largest-remainder allocation is
  the only way to keep `Σ LineDiscountsMinor == DiscountMinor` exactly; a naive `total * share / subtotal` per
  line loses pennies and breaks `Net + Ship + Tax = Gross`. (c) An **Exclusive promotion never combines with
  anything**, including other exclusives. (d) `shippingMinor` passed in must be the shipping the cart would
  otherwise pay — pass `0` when the cart already ships free, so "free shipping" scores no phantom benefit.
- **VALIDATE**: `dotnet build src/Services/Ordering/Domain/3commerce.Ordering.Domain.csproj -warnaserror`

### 17. UPDATE `src/Services/Ordering/Domain/Pricing.cs`

- **IMPLEMENT**:
  1. Append `Threshold = 9` to `PromotionKind` (line 19-ish, after `QuantityTier = 8`).
  2. Append to the `Promotion` record (after `bool Active = true`, so every existing positional/named
     construction in `PricingTests` still compiles):
     `long MinimumAmountMinor = 0, bool Combinable = false, bool GrantsFreeShipping = false,
      string Currency = "", DateTimeOffset? ActiveFrom = null, DateTimeOffset? ActiveUntil = null`.
  3. Extend `Promotion.Validate` (`:58-79`): `MinimumAmountMinor >= 0`; for `Kind == Threshold` require at
     least one threshold and at least one reward, and `ProductId is not null` when scoped to a product.
  4. Widen `PricingResult` (`:82-90`): add `IReadOnlyList<Guid> AppliedPromotionIds` and keep
     `AppliedPromotionId` as a **computed** property `AppliedPromotionIds.Count > 0 ? AppliedPromotionIds[0] : null`
     — do NOT remove it (existing tests at `PricingTests.cs:32,48,97,119,134` assert on it).
  5. **REFACTOR** `PricingEngine.Price` (`:149-180`): replace the inline `.Where(IsEligible).Select(Evaluate)
     .OrderByDescending(...).FirstOrDefault()` block at `:159-164` with a call into `PromotionEvaluator`,
     adapting `Promotion` → `PromotionCopy`-shaped candidates and `PricingLineInput` → `PromotionLine`
     (`SellingPriceMinor` is already the effective price by contract — see the comment at `:30-33`).
     Keep the legacy kinds (`Coupon*`, `Automatic*`, `Bundle*`, `QuantityTier`, `FreeShipping`) working by
     routing them through the same evaluator with their existing scope/threshold semantics mapped onto the new
     shape; every existing `PricingTests` assertion must still pass unchanged.
  6. Leave the storefront-discount block (`:166-179`) exactly as it is — it stacks additively **after** the
     promotion and the pair is capped at the subtotal.
- **PATTERN**: the file itself; the storefront-discount stacking comment at `:166-169` is the model for the
  comment you write above the promotion call.
- **IMPORTS**: none new.
- **GOTCHA**: This is the highest-risk edit in the plan. Run `PricingTests` **before** and **after** and diff
  the results — all 20 existing tests must stay green with **no edits to their assertions**. If routing a
  legacy kind through the evaluator proves awkward, keep the legacy switch for kinds 1–8 and call the
  evaluator only for `Threshold`, then combine the two candidate sets in the selection step — that is an
  acceptable, explicitly-allowed fallback, but say so in a code comment.
- **VALIDATE**: `dotnet test src/Services/Ordering/tests/3commerce.Ordering.Tests.csproj --filter FullyQualifiedName~PricingTests`

### 18. CREATE `src/Services/Ordering/tests/PromotionEvaluatorTests.cs`

- **IMPLEMENT**: xUnit facts, one per rule:
  - money threshold met / not met (storefront scope);
  - quantity threshold met / not met (storefront scope);
  - **both set → AND** (money met but quantity short ⇒ no discount);
  - product scope measures **only that product's** value and quantity;
  - product-scoped discount touches **only that product's lines** (assert `LineDiscountsMinor`);
  - percent reward vs fixed-amount reward; fixed amount **clamped to its own scope base**;
  - free-shipping-only promotion → `DiscountMinor == 0`, `FreeShippingApplied == true`;
  - **exclusive beats a smaller combinable pair**; **two combinables beat a bigger single exclusive**;
    **tie → the combinable set wins** (assert `AppliedPromotionIds.Count == 2`);
  - free-shipping value counts toward benefit (a free-shipping exclusive beats a small combinable discount
    when `shippingMinor` is large, and loses when `shippingMinor == 0`);
  - **currency mismatch is ineligible** (EUR promotion, AUD cart);
  - **window**: not-yet-started and expired promotions are ineligible; open-ended bounds apply;
  - **storefront mismatch** ineligible; **all-storefront (null)** applies;
  - `Inactive` ineligible;
  - **allocation sums exactly**: `Σ LineDiscountsMinor == DiscountMinor` for an odd subtotal that forces
    rounding (e.g. 3 lines of 333 with 10% off);
  - combined discount capped at the subtotal.
- **PATTERN**: `src/Services/Ordering/tests/PricingTests.cs:1-7` (class + engine field), `:291-308` (private
  `NewInput`/`Line` builders — write `Promo(...)` and `Line(...)` helpers in the same style), `:196-210` (the
  explanatory comment + explicit money identity assertion).
- **IMPORTS**: `using ThreeCommerce.Ordering.Domain;` — `namespace ThreeCommerce.Ordering.Tests;`
- **GOTCHA**: Build `PromotionCopy` instances directly (it is a plain settable class) — no EF needed. Use
  fixed `Guid`s where ordering matters so the ascending-`PromotionId` tiebreak is deterministic.
- **VALIDATE**: `dotnet test src/Services/Ordering/tests/3commerce.Ordering.Tests.csproj --filter FullyQualifiedName~PromotionEvaluatorTests`

### 19. UPDATE `src/Services/Ordering/tests/PricingTests.cs`

- **IMPLEMENT**: Add engine-level facts on top of the existing ones (do NOT modify existing assertions):
  - a `Threshold` promotion at storefront scope reaching a money threshold discounts the items only and holds
    `Net + Ship + Tax = Gross`;
  - a `Threshold` free-shipping promotion zeroes `ShippingMinor` and sets `FreeShippingApplied`;
  - a `Threshold` promotion **stacks with the storefront-wide discount** additively and the pair is capped at
    the subtotal (mirror `:225-253`);
  - the combined discount shrinks the taxable base in **both** regimes (mirror `:255-281`);
  - `AppliedPromotionIds` carries two ids when two combinables win, and `AppliedPromotionId` still returns the
    first.
  Extend the `NewInput` helper with a `promotions` parameter if needed.
- **PATTERN**: `src/Services/Ordering/tests/PricingTests.cs:196-281` — the six PR #256 tests, each opening
  with a comment that states the arithmetic in words before asserting it.
- **IMPORTS**: none new.
- **GOTCHA**: Every money test **must** carry the explicit `Net + Ship + Tax = Gross` assertion
  (`:208-209`) — it is the invariant CI's money gate relies on.
- **VALIDATE**: `dotnet test src/Services/Ordering/tests/3commerce.Ordering.Tests.csproj`

### 20. UPDATE `src/Services/Ordering/Domain/CheckoutAttempt.cs`

- **IMPLEMENT**: Add to `CheckoutAttempt` (after `DiscountMinor` at `:23`):
  `public long PromotionDiscountMinor { get; init; }` and
  `public string? AppliedPromotionIds { get; init; }` (comma-joined GUIDs, max 400) and
  `public bool FreeShippingApplied { get; init; }`. Carry all three through the projection/`ToOrder` mapping
  around `:61-97`. `CheckoutAttemptLine.DiscountMinor` already exists at `:117` — no schema change, it is
  simply no longer always 0.
- **PATTERN**: `src/Services/Ordering/Domain/CheckoutAttempt.cs:20-23` (money fields) and `:61-97` (the
  mapping that must copy every new field).
- **IMPORTS**: none new.
- **GOTCHA**: If the mapping at `:61-97` copies fields explicitly, a new field silently defaults to 0/null on
  the produced `Order` — assign it there too, and check whether `OrderLine` needs the same treatment at `:97`.
- **VALIDATE**: `dotnet build src/Services/Ordering/Domain/3commerce.Ordering.Domain.csproj -warnaserror`

### 21. CREATE the Ordering `CheckoutAttemptPromotions` migration

- **IMPLEMENT**:
  ```bash
  dotnet ef migrations add CheckoutAttemptPromotions \
    --project src/Services/Ordering/Infrastructure/3commerce.Ordering.Infrastructure.csproj \
    --startup-project src/Services/Ordering/Api/3commerce.Ordering.Api.csproj
  dotnet format src/Services/Ordering/Infrastructure/3commerce.Ordering.Infrastructure.csproj
  ```
- **PATTERN**: `src/Services/Catalog/Infrastructure/Migrations/20260831100446_StorefrontDiscount.cs:11-20` —
  `AddColumn` with `nullable: false, defaultValue: 0` for the money/bool columns; nullable for the id string.
- **IMPORTS**: generated.
- **GOTCHA**: `dotnet format` immediately (task 5). Adding a non-nullable column to an existing table **must**
  carry a `defaultValue` or the migration fails on a populated database.
- **VALIDATE**: `dotnet ef migrations has-pending-model-changes --project src/Services/Ordering/Infrastructure/3commerce.Ordering.Infrastructure.csproj --startup-project src/Services/Ordering/Api/3commerce.Ordering.Api.csproj && dotnet format src/Services/Ordering/Infrastructure/3commerce.Ordering.Infrastructure.csproj --verify-no-changes`

### 22. UPDATE `src/Services/Ordering/Api/Endpoints/CheckoutEndpoints.cs`

- **IMPLEMENT**, in the exact order of the "Pricing order contract":
  1. After the `offerCopies` load (`:74-76`), load promotions:
     ```csharp
     var promotionCopies = await db.PromotionCopies.AsNoTracking()
         .Where(p => p.TenantId == checkoutTenantId && p.Active
             && (p.StorefrontId == null || p.StorefrontId == storefrontId))
         .ToListAsync(ct);
     ```
  2. After `subtotal` (`:162`) and the `storefrontDiscountMinor` block (`:171-180`), and **after**
     `shippingMinor` is finalised (`:213-241`, including `allShippingCovered`), call the evaluator with the
     final shipping amount:
     ```csharp
     var promotionLines = cart.Items
         .Select(i => new PromotionLine(i.ProductId, i.UnitPriceMinor, i.Quantity)).ToList();
     var promotionOutcome = PromotionEvaluator.Evaluate(
         promotionLines, promotionCopies, checkoutTenantId, storefrontId, currency, shippingMinor, now);
     ```
     **Move** the storefront-discount computation to sit beside it so `discountMinor` becomes
     `Math.Clamp(promotionOutcome.DiscountMinor + storefrontDiscountMinor, 0, subtotal)`.
  3. `if (promotionOutcome.FreeShippingApplied) { shippingMinor = 0; }` — placed **after** the quote guards
     (`:223-235`) so a promotion never bypasses quote validation.
  4. Replace the approximate `taxableDiscountMinor` at `:262` with an exact per-line sum: for each cart line,
     its promotion allocation from `promotionOutcome.LineDiscountsMinor[i]` plus its proportional share of the
     storefront discount, counted only when `RuleFor(i.ProductId) is not { ChargeDestinationTax: false }`.
     Keep a `Math.Min(taxableDiscountMinor, taxableSubtotal)` clamp.
  5. Populate `CheckoutAttempt.PromotionDiscountMinor`, `.AppliedPromotionIds` (`string.Join(',', …)`, null
     when empty), `.FreeShippingApplied`; set each `CheckoutAttemptLine.DiscountMinor` from
     `promotionOutcome.LineDiscountsMinor[i]` (replacing the hard-coded `0` at `:375`).
  6. Append to `CheckoutResponse` (`:464`): `bool FreeShippingApplied = false, IReadOnlyList<Guid>? AppliedPromotionIds = null`
     and pass them at both the 409 (`:280-281`) and 201 (`:391-392`) construction sites.
- **PATTERN**: `src/Services/Ordering/Api/Endpoints/CheckoutEndpoints.cs:171-180` (the PR #256 discount block
  — same comment density), `:258-263` (the proportional tax allocation you replace), `:363-382` (the line
  projection).
- **IMPORTS**: none new — `ThreeCommerce.Ordering.Domain` is already imported at `:9`.
- **GOTCHA**: (a) `shippingMinor` is only final **after** `allShippingCovered` (`:238-241`) — evaluating
  promotions earlier would score free shipping against a rate the cart never pays. (b) The
  collect-at-warehouse / non-shippable paths already force `shippingMinor = 0` (`:220`) — free shipping is
  then a no-op benefit of 0, which is correct. (c) `CheckoutResponse` is a **positional record**: new
  parameters must be appended with defaults or every call site and the storefront client break. (d) Do not
  reorder the `priceChanged` 409 (`:277-282`) — it must still short-circuit before authorization.
- **VALIDATE**: `dotnet build src/Services/Ordering/Api/3commerce.Ordering.Api.csproj -warnaserror && dotnet test src/Services/Ordering/tests/3commerce.Ordering.Tests.csproj`

### 23. UPDATE `tests/3commerce.IntegrationTests/MoneyFlowTests.cs`

- **IMPLEMENT**: Add a `WaitForPromotionCopyAsync(Guid promotionId, …)` helper and these end-to-end tests,
  each on its **own unused currency**:
  1. `Threshold_promotion_grants_free_shipping_when_the_money_threshold_is_met` — publish a storefront config
     + a `PromotionChanged` (storefront scope, `MinimumAmountMinor`, `GrantsFreeShipping: true`); check out a
     cart above the threshold; assert `ShippingMinor == 0`, `DiscountMinor == 0`, gross = items, then
     `SimulatePaymentAsync` → `WaitForStatusAsync(… "Confirmed")` → `Assert.Equal(0, await fixture.TrialBalanceAsync())`.
  2. `Threshold_promotion_below_the_threshold_charges_full_price` — the control: same promotion, smaller cart,
     full shipping, no discount, trial balance 0.
  3. `Product_scoped_threshold_discounts_only_that_products_lines` — two products in the cart; assert
     `DiscountMinor` equals the percentage of **only** the scoped product's line total.
  4. `Threshold_promotion_measures_the_offer_resolved_price_not_the_catalog_price` — publish an `OfferChanged`
     pricing the product below its catalog price (mirror `MoneyFlowTests.cs:419-458`) so the cart falls
     **below** the threshold on the offer price though it would clear it on the catalog price; assert no
     discount. This is the single most important test in the feature.
  5. `Combinable_promotions_stack_and_beat_a_smaller_exclusive` — three promotions; assert the summed discount.
  6. `Promotion_stacks_with_the_storefront_wide_discount_and_tax_follows_the_discounted_base` — assert
     `TaxMinor` on the doubly-discounted base and `Net + Ship + Tax == Gross`, trial balance 0.
- **PATTERN**: `tests/3commerce.IntegrationTests/MoneyFlowTests.cs:356-458` (the three PR #256 tests — copy
  their structure, comment style and assertion order verbatim), `:228-240` (`WaitForOfferPriceCopyAsync`),
  `:460-475` (`WaitForDiscountCopyAsync`), `:28` (`Checkout()`).
- **IMPORTS**: `using ThreeCommerce.BuildingBlocks.Contracts.Catalog;` is already present (for
  `StorefrontConfigChanged`/`OfferChanged`).
- **GOTCHA**: (a) **Pick a currency no other test uses** (`MoneyFlowTests` already claims NOK/DKK/ZAR etc.) —
  a shared live tax copy makes tests interfere. (b) A promotion is only effective when its `Currency` matches
  the cart, so publish it in the same currency as the storefront. (c) `fixture.ApproveSupplierAsync` is
  required before any offer prices (ADR-0048). (d) Projections are async — poll.
- **VALIDATE**: `dotnet test tests/3commerce.IntegrationTests/3commerce.IntegrationTests.csproj --filter FullyQualifiedName~MoneyFlowTests`

### 24. UPDATE `src/Services/Ordering/Api/Endpoints/CartEndpoints.cs`

- **IMPLEMENT**: Add `group.MapGet("/summary", GetSummary);` and a handler
  `GetSummary(Guid? storefrontId, HttpContext http, CartService carts, OrderingDbContext db, TimeProvider time, CancellationToken ct)`
  that: resolves tenant/storefront the same way checkout does (`CheckoutEndpoints.cs:52-57`); loads
  `OfferCopies` + `SupplierApprovalCopies` + `StorefrontTaxCopies` + `PromotionCopies`; recomputes each line's
  effective price via `OfferResolution.ResolvePricingOffer` (identical to `CheckoutEndpoints.cs:114-123`);
  calls `PromotionEvaluator.Evaluate` with the flat `FlatShippingMinor` fallback (499) as the shipping value;
  returns
  ```csharp
  public record CartSummaryResponse(
      long SubtotalMinor, long StorefrontDiscountMinor, long PromotionDiscountMinor,
      long ItemsTotalMinor, bool FreeShippingApplied,
      List<AppliedPromotionResponse> AppliedPromotions, string Currency);
  public record AppliedPromotionResponse(Guid PromotionId, string Name, long DiscountMinor);
  ```
- **PATTERN**: `src/Services/Ordering/Api/Endpoints/CartEndpoints.cs:20` (route registration), `:51-57`
  (`GetCart`), `:195-209` (`ToResponse` + records); `CheckoutEndpoints.cs:72-129` (offer resolution to copy).
- **IMPORTS**: `using Microsoft.EntityFrameworkCore;` and `using ThreeCommerce.Ordering.Domain;` (verify what
  the file already imports).
- **GOTCHA**: (a) **Do not change `GET /cart`** — many callers depend on its shape; `/summary` is additive.
  (b) `/cart/summary` must be **anonymous-friendly** exactly like `GET /cart` (it is cookie-keyed).
  (c) The preview uses the flat fallback shipping, so the *free-shipping benefit* it scores can differ from a
  checkout where the shopper picked an expensive carrier rate; comment that the discount figure (the part
  shown) is unaffected. (d) This is the ONLY place the storefront learns the promotion result — never
  re-implement the algorithm in TypeScript.
- **VALIDATE**: `dotnet build src/Services/Ordering/Api/3commerce.Ordering.Api.csproj -warnaserror`

### 25. UPDATE `src/Storefront/lib/gateway.ts`

- **IMPLEMENT**: Add
  ```ts
  export type AppliedPromotionDto = { promotionId: string; name: string; discountMinor: number };
  export type CartSummaryDto = {
    subtotalMinor: number; storefrontDiscountMinor: number; promotionDiscountMinor: number;
    itemsTotalMinor: number; freeShippingApplied: boolean;
    appliedPromotions: AppliedPromotionDto[]; currency: string;
  };
  export async function getCartSummary(storefrontId?: string): Promise<CartSummaryDto | null>
  ```
  fetching `/api/ordering/cart/summary` with `{ cache: "no-store" }` and returning `null` on a non-OK response
  (callers then fall back to the plain cart, no promotion rows).
- **PATTERN**: `src/Storefront/lib/gateway.ts:145-175` (the `getStorefrontConfig` raw-cast + defaulting shape)
  and `:250-258` (`getCart` with its offline fallback).
- **IMPORTS**: reuse the existing `gatewayFetch` helper in the same file.
- **GOTCHA**: The cart is **cookie-keyed** — the fetch must forward cookies the same way `getCart` does; copy
  its call shape exactly. Never cache (`cache: "no-store"`).
- **VALIDATE**: `cd src/Storefront && npx tsc --noEmit`

### 26. UPDATE `src/Storefront/app/cart/page.tsx`

- **IMPLEMENT**: Call `getCartSummary()` alongside `getCart()`. Below the existing subtotal / storefront
  discount rows, render one row per applied promotion —
  `{t("promotion", { name: p.name })}` with `−{formatMoney(p.discountMinor, cart.currency)}` — plus a
  `{t("freeShipping")}` badge row when `summary.freeShippingApplied`, and base the "items total" row on
  `summary.itemsTotalMinor` when a summary is available (falling back to today's local math when it is null).
- **PATTERN**: `src/Storefront/app/cart/page.tsx:19-26` (the discount computation) and `:44-66` (the rows,
  including the `text-emerald-700` deduction styling and the `−` sign).
- **IMPORTS**: `import { getCart, getCartSummary } from "@/lib/gateway";`
- **GOTCHA**: The page is a server component and already `no-store`; keep it that way. When `summary` is
  `null` the page must render exactly as it does today — no crash, no empty rows.
- **VALIDATE**: `cd src/Storefront && npx tsc --noEmit && npm run lint`

### 27. UPDATE `src/Storefront/app/checkout/page.tsx` + `src/Storefront/components/checkout/CheckoutForm.tsx`

- **IMPLEMENT**: `checkout/page.tsx` fetches the summary and threads `promotionDiscountMinor`,
  `freeShippingApplied` and `appliedPromotions` into `<CheckoutForm …>`. `CheckoutForm` adds them to
  `CheckoutFormProps`, renders a `<Row>` per applied promotion under the existing discount row, forces the
  displayed shipping to the free label when `freeShippingApplied`, and subtracts the promotion discount from
  `discountedSubtotalMinor` **before** `taxBaseMinor` so the estimate matches Ordering's charge.
- **PATTERN**: `src/Storefront/components/checkout/CheckoutForm.tsx:21-26` (prop + doc comment), `:38` (the
  destructure), `:65-80` (the money math and its comment), `:110-120` (the `<Row>` summary block);
  `src/Storefront/app/checkout/page.tsx:26-42` (the taxSource plumbing).
- **IMPORTS**: `getCartSummary` from `@/lib/gateway`.
- **GOTCHA**: The existing `collect` / `selectedRate` shipping logic already yields 0 for collect-at-warehouse
  — free shipping must not double-handle it; prefer `const shippingShown = freeShippingApplied ? 0 : …`.
- **VALIDATE**: `cd src/Storefront && npx tsc --noEmit && npm run lint && npm run build`

### 28. UPDATE `src/Storefront/messages/{en,de,es,fr,yue,zh}.json`

- **IMPLEMENT**: Add `cart.promotion` (`"Promotion: {name}"`), `cart.freeShipping` (`"Free shipping"`) and
  `checkout.promotion`, `checkout.freeShipping` to **all six** files, translated (not English placeholders).
- **PATTERN**: `src/Storefront/messages/en.json:135-140` (`cart.discount`, `cart.itemsTotal`) and `:156-160`
  (`checkout.discount`) — the exact keys PR #256 added across all six locales.
- **IMPORTS**: n/a.
- **GOTCHA**: A key present in `en.json` but missing elsewhere throws at render for that locale. Keep the ICU
  argument name `{name}` identical in every file.
- **VALIDATE**: `for f in src/Storefront/messages/*.json; do node -e "const m=require('./$f'); if(!m.cart.promotion||!m.checkout.promotion){console.error('missing in $f');process.exit(1)}"; done && echo OK`

### 29. CREATE `src/Admin/Components/Pages/Promotions.razor`

- **IMPLEMENT**: `@page "/promotions"`, admin-only, interactive-server-no-prerender. A tenant-id + storefront
  filter row with Load and "New promotion" buttons; a table with columns Name, Scope, Product, Storefront,
  Threshold, Reward, Combinable, Window, Status, Actions; a modal create/edit form with: Name, Currency
  (`<CurrencySelect>`), Scope `<select>` (1 Storefront / 2 Product), a **product picker reusing the
  `SafeProducts()` search dropdown** (enabled only when Scope == Product), a Storefront `<select>` fed by
  `SafeStorefronts()` (blank = all), `Minimum amount` (major-unit input → minor on save) and
  `Minimum quantity`, a `Free shipping` checkbox, `Percent off` and `Fixed discount` inputs, a `Combinable`
  checkbox, `ActiveFrom`/`ActiveUntil` date inputs, and (edit only) an `Active` checkbox. Save POSTs/PUTs
  `/api/catalog/admin/promotions`. Every label and tooltip goes through `L["Promotions.…"]`.
- **PATTERN**: `src/Admin/Components/Pages/Offers.razor:1-4` (directives), `:9-21` (filter row + status/error
  paragraphs), `:51-58` and `:171-174` (modal chrome + button row), **`:138-156` (copy the storefront picker
  and the two date inputs verbatim)**, `:212-232` (`Save` — including the `AddDays(1).AddTicks(-1)` end-day
  trick at `:224` and the "set `_status` AFTER `LoadAsync`" rule at `:230`), `:265-338` (`Safe*()` helpers),
  `:355-368` (the `_form` class + private record DTOs at the bottom).
  `src/Admin/Components/Pages/CommerceOps.razor:655-658` for a `—`-when-zero label helper.
- **IMPORTS**: none beyond the `@inject GatewayClient Gateway` the page directive supplies; the private DTO
  records live at the bottom of `@code` (never a separate file).
- **GOTCHA**: (a) **Enums cross HTTP as numbers** — bind `Scope` to an `int` field. (b) Money entry is in
  **minor units** on the wire; if you accept major units in the UI, convert on save and document it in the
  tooltip, and state that the threshold excludes tax and shipping (AGENTS.md:263). (c) `SafeProducts()` asks
  for `?pageSize=200` (`Offers.razor:270`) — the server clamps there. (d) Failures in the dropdown loaders must
  be swallowed so the modal still opens.
- **VALIDATE**: `dotnet build src/Admin/3commerce.Admin.csproj -warnaserror`

### 30. UPDATE `src/Admin/Components/Layout/MainLayout.razor`

- **IMPLEMENT**: Add `<li><NavLink href="/promotions" style="color:#cbd5e1;" title="@L["Nav.Promotions.Tip"]">@L["Nav.Promotions"]</NavLink></li>`
  immediately after the `/offers` entry.
- **PATTERN**: `src/Admin/Components/Layout/MainLayout.razor:13`.
- **IMPORTS**: none.
- **GOTCHA**: **Adding a nav item shifts nothing in a table, but adding a table COLUMN does** — see the
  regression this repo already suffered (`storefront-lifecycle.spec.ts` broke when the Discount column shifted
  `State` from `td[5]` to `td[6]`, fixed in the second commit of PR #256). You are creating a **new** page, so
  no existing table shifts — but task 38 still re-runs the admin specs to prove it.
- **VALIDATE**: `dotnet build src/Admin/3commerce.Admin.csproj -warnaserror`

### 31. UPDATE `src/Admin/Resources/SharedResource{,.de,.es,.fr,.yue,.zh}.resx`

- **IMPLEMENT**: Add every `Promotions.*` key used by task 29 plus `Nav.Promotions` / `Nav.Promotions.Tip` to
  **all six** files, translated. Include `.Tip` entries for every field.
- **PATTERN**: `src/Admin/Resources/SharedResource.resx:815-816` (`Ops.Field.Discount` +
  `Ops.Field.Discount.Tip` — note how the tip states the exact commerce semantics) and `:839`
  (`Ops.Col.Discount`).
- **IMPORTS**: n/a (XML).
- **GOTCHA**: A missing key renders as the raw key string in the UI and will fail the Playwright locators in
  task 32. Add the keys **in the same relative position** in every file so future diffs stay readable.
- **VALIDATE**: `for f in src/Admin/Resources/SharedResource*.resx; do grep -q 'name="Nav.Promotions"' "$f" || { echo "missing in $f"; exit 1; }; done && echo OK && dotnet build src/Admin/3commerce.Admin.csproj -warnaserror`

### 32. CREATE `src/Storefront/e2e-admin/promotions-admin.spec.ts`

- **IMPLEMENT**: Log in via the gateway API, seed a storefront + a product (or reuse the demo tenant),
  `loginAsAdmin(page)`, `page.goto("/promotions")`, create a threshold promotion through the modal, assert the
  new row renders its threshold and reward, then round-trip via `GET /api/catalog/admin/promotions` and assert
  `minimumAmountMinor` / `percentOff` / `combinable`. Screenshot before and after.
- **PATTERN**: `src/Storefront/e2e-admin/commerce-ops-discount.spec.ts:1-56` (whole file) — API seeding,
  `loginAsAdmin`, `expect(row).toBeVisible({ timeout: 10_000 })`, the **`press("Tab")` Blazor-bind commit**,
  screenshots, and the final API assertion. Also `src/Storefront/e2e-admin/offers-title-modal.spec.ts` for
  driving a modal form.
- **IMPORTS**: `import { test, expect } from "@playwright/test"; import { loginAsAdmin } from "./helpers";`
- **GOTCHA**: (a) `press("Tab")` after **every** `fill()` on a Blazor-bound input, or the value never reaches
  the circuit. (b) Scope `getByLabel(...)` with `.nth(n)` when the create and edit forms share a label
  (`commerce-ops-discount.spec.ts:37`). (c) The full-suite run hits the gateway's single-IP auth rate limit —
  `run-all.sh` raises it for dev/E2E; do not add extra logins beyond what the helper does.
- **VALIDATE**: `cd src/Storefront && npx playwright test e2e-admin/promotions-admin.spec.ts`

### 33. CREATE `src/Storefront/e2e/storefront-promotions.spec.ts`

- **IMPLEMENT**: Create a threshold promotion on a demo storefront via the admin API; `expect.poll` until
  `/cart/summary` reports it; add enough of a product to clear the threshold; assert the cart shows the
  promotion row and the discounted items total, and (for a free-shipping promotion) that checkout shows free
  shipping; **then delete/deactivate the promotion** so sibling specs see the baseline.
- **PATTERN**: `src/Storefront/e2e/storefront-discount.spec.ts:1-60` (whole file) — mutate via API, poll the
  public surface, shop in the browser, assert, and **reset in a `finally`**.
- **IMPORTS**: `@playwright/test` plus the spec's local gateway helpers.
- **GOTCHA**: **Always reset the demo store's state** — a leftover promotion silently changes every other
  spec's totals. Put the reset in `finally`.
- **VALIDATE**: `cd src/Storefront && npx playwright test e2e/storefront-promotions.spec.ts`

### 34. UPDATE `scripts/e2e-verify.sh`

- **IMPLEMENT**: (a) Extend the A3 bullet in the COVERAGE CHECKLIST header (`scripts/e2e-verify.sh:27-33`) to
  name the threshold promotions, combinability selection and the shared evaluator; add a line for the Catalog
  `PromotionTests`. (b) Add `PromotionProjectionTests` to the relevant integration bullet and the promotion
  money-flow cases to the A6c bullet. (c) Extend the L20 bullet (`:117-122`) to name the promotions admin page
  and the storefront promotion flow. (d) Add/adjust the matching `check`/filter invocations in the script body
  so the new suites actually run.
- **PATTERN**: `scripts/e2e-verify.sh:16-18` (the "keep in sync" instruction), `:27-33` (the existing pricing
  sentence), `:117-122` (the L20 sentence).
- **IMPORTS**: n/a (bash).
- **GOTCHA**: AGENTS.md:240 makes this **mandatory in the same change** — both the checklist comment *and* the
  executable check. The script must still pass afterwards.
- **VALIDATE**: `bash -n scripts/e2e-verify.sh && grep -qi "promotion" scripts/e2e-verify.sh && echo OK`

### 35. UPDATE `docs/api/api_contracts_index.md` (+ the OpenAPI files)

- **IMPLEMENT**: Add a Catalog section row set for `GET/POST /admin/promotions` and `PUT /admin/promotions/{id}`
  (auth `admin`), and an Ordering row for `GET /cart/summary` (auth `anonymous`). Regenerate or hand-extend
  `docs/api/catalog.openapi.json` and `docs/api/ordering.openapi.json` to match.
- **PATTERN**: `docs/api/api_contracts_index.md` (the existing `| Method | Path | Auth | Purpose |` tables at
  the end of the file).
- **IMPORTS**: n/a.
- **GOTCHA**: AGENTS.md:211 + :230 require this for **any** endpoint change. Keep the `Purpose` column to one
  line.
- **VALIDATE**: `grep -c "admin/promotions" docs/api/api_contracts_index.md`

### 36. UPDATE `docs/help/services.html`

- **IMPLEMENT**: Add the promotions endpoints to the Catalog service card and `/cart/summary` to the Ordering
  card.
- **PATTERN**: the existing Catalog/Ordering endpoint lists in the same file.
- **IMPORTS**: n/a.
- **GOTCHA**: AGENTS.md:230 lists `docs/help/services.html` alongside the API index for any endpoint change.
- **VALIDATE**: `grep -c "promotions" docs/help/services.html`

### 37. UPDATE `.ai-shared/plans/plan_status_executions.md`

- **IMPLEMENT**: Flip each task's row `TODO → in_progress → done` **as you work**, recording deviations,
  GOTCHAs hit, and the PR number in the Comments column. Refresh the `Last Modified Date-Time` header.
- **PATTERN**: `.ai-shared/plans/plan_status_executions.md` (the established table).
- **IMPORTS**: n/a.
- **GOTCHA**: AGENTS.md:238 — this is canonical; do **not** keep status only in todos and do **not** create a
  side `*-followups.md`. Update it in the same change as the work, not at the end.
- **VALIDATE**: `grep -c "threshold-promotions.md" .ai-shared/plans/plan_status_executions.md`

### 38. Full regression — build, format, unit, integration, storefront, E2E

- **IMPLEMENT**: Run the complete gate set (VALIDATION COMMANDS below), **including re-running every existing
  admin Playwright spec** (`e2e-admin/`), not just the new ones.
- **PATTERN**: `AGENTS.md:294` (the validation one-liner) and `scripts/e2e-verify.sh`.
- **IMPORTS**: n/a.
- **GOTCHA**: (a) CI's `build-test` job enforces analyzers across **every** touched project including Domain —
  `dotnet format --verify-no-changes` at the solution level, not just the projects you remember. (b) The
  required CI gates are `changes` / `build-test` / `integration`, plus `compose-smoke`, `kind-deploy` and
  `browser-e2e`. (c) To pick up new binaries locally, bounce the whole stack
  (`scripts/run-all.sh stop && scripts/run-all.sh start`), not individual services.
- **VALIDATE**: `dotnet build 3commerce.sln && dotnet format --verify-no-changes && dotnet test 3commerce.sln && (cd src/Storefront && npm run lint && npx tsc --noEmit && npm run build) && scripts/e2e-verify.sh`

---

## TESTING STRATEGY

The repo tests money at four layers; this feature must appear in all four.

### Unit Tests

- **Catalog domain** (`src/Services/Catalog/tests/PromotionTests.cs`, xUnit, no infrastructure) — every
  aggregate invariant from task 3, asserted via `Assert.Throws<CatalogRuleException>` +
  `Assert.Contains(…, StringComparison.Ordinal)`.
- **Ordering domain** (`src/Services/Ordering/tests/PromotionEvaluatorTests.cs`) — the full truth table of
  task 18. Fixtures are plain `PromotionCopy` objects and `PromotionLine` structs; use fixed `Guid`s wherever
  the ascending-id tiebreak matters.
- **Pricing engine** (`src/Services/Ordering/tests/PricingTests.cs`) — the new engine-level facts of task 19,
  each carrying the explicit `Net + Ship + Tax = Gross` assertion (`PricingTests.cs:208-209`).
- **Regression bar**: all 20 pre-existing `PricingTests` must pass **unmodified** after the task-17 refactor.

### Integration Tests

Testcontainers (real Postgres + RabbitMQ), `tests/3commerce.IntegrationTests`, `Category=Integration`.

- `PromotionProjectionTests` — `PromotionChanged` → `PromotionCopy` insert, idempotent re-consume,
  deactivation.
- `MoneyFlowTests` — the six end-to-end cases of task 23. Every one ends with
  `Assert.Equal(0, await fixture.TrialBalanceAsync())` after `SimulatePaymentAsync` + `WaitForStatusAsync`.
  Each test claims its **own unused currency** so live storefront copies never cross-contaminate.

### Browser E2E (Playwright)

- `e2e-admin/promotions-admin.spec.ts` — create + edit a promotion through the admin modal, round-tripped via
  the API.
- `e2e/storefront-promotions.spec.ts` — the shopper sees the promotion row and free shipping in the cart, then
  the demo store is reset.
- **Plus a re-run of every existing `e2e-admin/` spec** — a prior PR broke `storefront-lifecycle.spec.ts` by
  shifting a table column, so admin-page changes are never assumed safe.

### Edge Cases

- Threshold **exactly equal** to the minimum (`>=`, not `>`).
- Both thresholds set: money met, quantity short ⇒ **ineligible** (AND).
- Product-scoped promotion whose product is **not** in the cart ⇒ ineligible.
- Product-scoped promotion where the product appears on **several variant lines** ⇒ quantities and values sum
  across them.
- Fixed discount **larger than its own scope base** ⇒ clamped to the base.
- Promotion + storefront-wide discount together exceeding the subtotal ⇒ clamped to the subtotal, gross never
  negative (`PricingTests.cs:240-253` is the precedent).
- Free-shipping promotion on a cart that already ships free (non-shippable, collect-at-warehouse, or
  `allShippingCovered`) ⇒ benefit 0, no crash, shipping stays 0.
- Free-shipping promotion vs a discount promotion where the shipping value decides the winner — and the
  mirror case with `shippingMinor == 0`.
- **Currency mismatch** (EUR promotion, AUD cart) ⇒ ineligible (no-FX invariant).
- Window boundaries: `ActiveFrom == now` and `ActiveUntil == now` are inclusive; open-ended nulls apply.
- Storefront-scoped vs all-storefront (null) promotions on the same tenant.
- An **unapproved supplier's** offer must not set the price feeding the threshold (ADR-0048).
- A cart line whose product is destination-tax-exempt (`ChargeDestinationTax: false`) receives an allocation
  that must **not** enter the tax base.
- Rounding: an odd subtotal (e.g. 3 × 333 at 10%) ⇒ `Σ LineDiscountsMinor == DiscountMinor` exactly.
- Two promotions with identical benefit ⇒ deterministic winner (combinable set wins; ids ascending).
- An older `PromotionChanged` message missing appended fields ⇒ back-compatible defaults, no crash.

---

## VALIDATION COMMANDS

Execute every command to ensure zero regressions and 100% feature correctness.

### Level 1: Syntax & Style

```bash
dotnet build 3commerce.sln
dotnet format --verify-no-changes
cd src/Storefront && npm run lint && npx tsc --noEmit
```

### Level 2: Unit Tests

```bash
dotnet test src/Services/Catalog/tests/3commerce.Catalog.Tests.csproj
dotnet test src/Services/Ordering/tests/3commerce.Ordering.Tests.csproj
dotnet test 3commerce.sln
```

### Level 3: Integration Tests

```bash
# Docker must be running (Testcontainers)
dotnet test tests/ --filter Category=Integration
dotnet test tests/3commerce.IntegrationTests/3commerce.IntegrationTests.csproj --filter FullyQualifiedName~PromotionProjectionTests
dotnet test tests/3commerce.IntegrationTests/3commerce.IntegrationTests.csproj --filter FullyQualifiedName~MoneyFlowTests

# Migration hygiene — both services must report no pending model changes
dotnet ef migrations has-pending-model-changes \
  --project src/Services/Catalog/Infrastructure/3commerce.Catalog.Infrastructure.csproj \
  --startup-project src/Services/Catalog/Api/3commerce.Catalog.Api.csproj
dotnet ef migrations has-pending-model-changes \
  --project src/Services/Ordering/Infrastructure/3commerce.Ordering.Infrastructure.csproj \
  --startup-project src/Services/Ordering/Api/3commerce.Ordering.Api.csproj
```

### Level 4: Manual Validation

```bash
# Boot the stack
scripts/run-all.sh start        # or: scripts/launch.sh --reuse --env dev

# 1. Create a threshold promotion (admin)
curl -s -X POST http://localhost:8080/api/catalog/admin/promotions \
  -H 'Content-Type: application/json' -b /tmp/3c-admin.jar \
  -d '{"tenantId":"00000000-0000-0000-0000-000000000001","name":"Spend 100 ship free",
       "currency":"EUR","scope":1,"minimumAmountMinor":10000,"grantsFreeShipping":true,
       "combinable":false}'

# 2. Confirm the projection landed in Ordering (read copy, ADR-0008)
docker exec -i 3c-postgres psql -U ordering -d ordering \
  -c 'select "PromotionId","Name","MinimumAmountMinor","GrantsFreeShipping","Active" from ordering."PromotionCopies";'

# 3. Shop it: add enough to clear the threshold, then read the preview
curl -s -c /tmp/3c-cart.jar -b /tmp/3c-cart.jar -X POST http://localhost:8080/api/ordering/cart/items \
  -H 'Content-Type: application/json' -d '{"productId":"<id>","quantity":3,"currency":"EUR"}'
curl -s -b /tmp/3c-cart.jar http://localhost:8080/api/ordering/cart/summary | jq

# 4. Check out and confirm shipping is zero and the ledger balances
curl -s -b /tmp/3c-cart.jar -X POST http://localhost:8080/api/ordering/checkout \
  -H 'Content-Type: application/json' -d @/tmp/checkout.json | jq '{DiscountMinor,ShippingMinor,TaxMinor,GrossMinor,FreeShippingApplied,AppliedPromotionIds}'
curl -s http://localhost:8080/api/payments/admin/ledger/trial-balance | jq   # must be 0

# 5. Browser: admin /promotions renders and saves; storefront cart shows the promotion row
open http://localhost:5080/promotions
open http://localhost:3000/cart
```

Manual checklist:
- [ ] Cart's shown discount **equals** the checkout response's `DiscountMinor` (shown == charged).
- [ ] A promotion in a different currency to the storefront never applies.
- [ ] Deactivating the promotion in admin removes it from the cart within a few seconds (projection).
- [ ] The storefront-wide discount and the promotion both appear as **separate lines** and stack.

### Level 5: Additional Validation (Optional)

```bash
# Full regression (automated) and full-stack live journeys
scripts/e2e-verify.sh
scripts/e2e-verify.sh --live

# Playwright: the new specs AND every pre-existing admin spec (column-shift regression guard)
cd src/Storefront && npx playwright test e2e-admin/ e2e/

# Local CI parity for the gates that block merge
scripts/ci-logs.sh   # after pushing, surfaces failing jobs + their error lines
```

---

## ACCEPTANCE CRITERIA

- [ ] A tenant admin can create, edit, activate and deactivate a threshold promotion at `/promotions` in the
      admin app, with every label and tooltip localized in all six locales.
- [ ] A promotion supports a **money threshold, a quantity threshold, or both (AND)**.
- [ ] `Storefront` scope measures the whole cart's item value/quantity and discounts **all** item lines;
      `Product` scope measures and discounts **only that product's** lines.
- [ ] The threshold and the discount are both computed on the **offer-resolved effective selling price ×
      quantity**, excluding tax, shipping and fees — proven by the dedicated `MoneyFlowTests` case where the
      offer price and the catalog price fall on opposite sides of the threshold.
- [ ] A promotion can grant **free shipping**, a **percentage** discount, a **fixed-amount** discount, or free
      shipping plus one of those.
- [ ] Every promotion carries a **combinable/exclusive** flag; the engine takes the better of
      `[best single exclusive]` vs `[sum of all combinable]` by customer benefit
      (`discount + freeShippingValue`), with a documented deterministic tiebreak.
- [ ] The **storefront-wide discount (PR #256) still always applies** and is unaffected by the combinability
      flag; the two stack additively and are jointly capped at the subtotal.
- [ ] The pricing order matches the normative "Pricing order contract" in this plan and is recorded in
      ADR-0051.
- [ ] `Net + Ship + Tax = Gross` holds in every unit and integration money test.
- [ ] `trial balance = 0` after payment in every new `MoneyFlowTests` case; **no new ledger line** was added.
- [ ] Promotions are **Catalog-owned and projected into Ordering**; checkout performs **no** cross-service
      query (ADR-0008); migrations exist in **both** services.
- [ ] `PricingEngine` and `CheckoutEndpoints` compute the promotion through **one shared
      `PromotionEvaluator`** — the algorithm exists in exactly one place
      (`grep -rn "Combinable" src/Services/Ordering --include=*.cs` shows the evaluator + the copy/record
      definitions only).
- [ ] The storefront cart and checkout summary show the applied promotions and free shipping, and the shown
      discount equals the charged discount.
- [ ] All 20 pre-existing `PricingTests` pass **unmodified**.
- [ ] All validation commands (Levels 1–3, 5) pass with zero errors; `scripts/e2e-verify.sh` is green.
- [ ] Every pre-existing `e2e-admin/` Playwright spec still passes.
- [ ] Docs updated in the same change: ADR-0051 + `adr_index.md`, `docs/api/api_contracts_index.md` (+ the two
      OpenAPI files), `docs/help/services.html`, `scripts/e2e-verify.sh` + its COVERAGE CHECKLIST, and
      `.ai-shared/plans/plan_status_executions.md`.

---

## COMPLETION CHECKLIST

- [ ] All tasks completed in order
- [ ] Each task validation passed immediately
- [ ] All validation commands executed successfully
- [ ] Full test suite passes (unit + integration)
- [ ] No linting or type checking errors
- [ ] Manual testing confirms feature works
- [ ] Acceptance criteria all met
- [ ] Code reviewed for quality and maintainability
- [ ] `dotnet format` run on **both** Infrastructure csprojs after every `ef migrations add`
- [ ] `plan_status_executions.md` rows all `done` with real Comments (deviations, GOTCHAs hit, PR #)
- [ ] Pre-existing admin Playwright specs re-run and green

---

## NOTES

### Design decision 1 — Catalog owns `Promotion` (rejected: the `Pricing` service)

`src/Services/Pricing` exists but is a dormant Phase-7 slice: a `Price` aggregate
(`src/Services/Pricing/Domain/Price.cs:16-25`, 113 lines) with an 81-line endpoint file, no bus contracts, no
consumers, and no path to checkout. Routing promotions through it would require a brand-new
Pricing → Ordering projection lane, a second admin gateway surface, and a cross-service dependency on
Catalog's storefront and product ids — for zero architectural gain. Catalog already owns `Storefront` and
`Offer` (the two things a promotion references), already publishes `StorefrontConfigChanged` and
`OfferChanged` into Ordering, and already hosts the admin endpoint group under `/api/catalog/admin/*`. If a
dedicated promotions service is ever wanted, the contract (`PromotionChanged`) is the seam that makes the move
cheap.

### Design decision 2 — extract a shared evaluator (rejected: duplicate the algorithm in checkout)

`PricingEngine` is not on the production money path: `CheckoutEndpoints.Checkout` re-implements subtotal,
discount, tax and gross inline (`CheckoutEndpoints.cs:162-275`) and never touches `PricingInput`/`PricingEngine`.
Two options existed:

- **Rejected — duplicate.** Write the promotion algorithm a second time inside `CheckoutEndpoints`. It is
  faster to type and requires no refactor, but it institutionalises the divergence: `PricingTests` would go
  green while checkout charged something else, which is exactly the failure mode this codebase already has.
- **Chosen — extract.** One pure `PromotionEvaluator` in `Ordering.Domain`, called by both. It is a small,
  well-bounded extraction (only the promotion decision moves, not the tax/shipping seams, which genuinely
  differ between the engine and checkout), it is unit-testable without infrastructure, and it makes the
  divergence impossible for the promotion logic specifically.

A **full** unification — deleting checkout's inline math and having it construct a `PricingInput` — is the
right long-term shape but is a materially bigger, riskier refactor (checkout also owns ship-rule exemptions,
collect-at-warehouse, approval gating, quote validation, per-currency carts and the price-drift 409, none of
which `PricingInput` models). It is deliberately **out of scope** here and should be its own ADR + PR.

### Design decision 3 — "both thresholds set ⇒ AND"

The user's phrasing was "$ amount and/or quantity". Two readings exist. **AND** is chosen because it is the
conservative one: a promotion that fires when *either* threshold is met is strictly more generous and cannot
be tightened later without changing behaviour for live campaigns, whereas AND can be relaxed. An operator who
wants OR creates two promotions and marks them combinable-or-exclusive as desired. This is recorded in
ADR-0051.

### Design decision 4 — tie-break: the combinable set wins

When `[best exclusive]` and `[sum of combinables]` score the same customer benefit, the combinable set wins.
It shows the shopper more applied promotions for the same money (better perceived value), and it is
deterministic. Within each branch, ties break on ascending `PromotionId`, mirroring the existing
`.ThenBy(p => p.PromotionId)` at `Pricing.cs:163`.

### Design decision 5 — per-line allocation instead of a proportional ratio

`CheckoutEndpoints.cs:262` currently apportions the discount to the tax base with
`discountMinor * taxableSubtotal / subtotal`. That is exact for a *uniform* storefront-wide percentage, but
wrong for a **product-scoped** promotion (whose discount may fall entirely on a tax-exempt line, or entirely
on a taxable one). Returning `LineDiscountsMinor` from the evaluator lets checkout sum only the allocations on
destination-taxable lines. Largest-remainder allocation keeps `Σ LineDiscountsMinor == DiscountMinor` exactly,
which is what preserves `Net + Ship + Tax = Gross`.

### Ledger

No new ledger line and no new account. Exactly as PR #256 established: the promotion lowers the charged gross,
so the sale posts less revenue and the trial balance stays 0. Every new `MoneyFlowTests` case asserts this
explicitly. The per-storefront attribution invariant (ADR-0045) is untouched — nothing about the accounts
changes.

### Explicitly out of scope (candidates for a follow-up)

- Coupon-code-gated threshold promotions (the `CouponFixed`/`CouponPercent` kinds already exist and are
  unchanged; wiring a coupon field through checkout is separate work).
- Per-shopper or global usage caps / redemption counting.
- "You're $X away from free shipping" progress nudges on the storefront.
- Category-scoped thresholds (`AutomaticCategory` exists in the enum; `PricingLineInput.CategoryId` is
  populated, but `CartItem` does not carry a category — adding it is its own slice).
- Buy-X-get-Y / bundle rewards.
- Full unification of `CheckoutEndpoints` onto `PricingEngine` (see Design decision 2).

### Project gotchas collected in one place

1. **`dotnet format` the Infrastructure csproj after every `ef migrations add`** — the generated designer and
   snapshot violate the analyzer rules and CI's format gate fails otherwise.
2. **Money is integer minor units** everywhere — no `decimal`/`double` in the domain; `checked` arithmetic.
3. **Publish before `SaveChangesAsync`** in Catalog endpoints, or the outbox row is stranded and the Ordering
   projection never fires (`OfferEndpoints.cs:117-121`).
4. **Consumer class names must be globally unique across services** — the queue name derives from the class
   name (AGENTS.md:234 item 3).
5. **Contracts are append-only** with back-compatible defaults; never reorder positional parameters.
6. **Enums cross HTTP as numbers.**
7. **After changing an admin page, re-run the existing admin specs** — a prior PR broke
   `storefront-lifecycle.spec.ts` by shifting a table column (`td[5]` → `td[6]`).
8. **Playwright + Blazor**: `press("Tab")` after `fill()` so `@bind` commits before the save click.
9. **Gateway auth rate limit** (30/min single IP) is raised for dev/E2E by `run-all.sh`; the full browser
   suite 429s without it. Don't add gratuitous logins.
10. **Integration-test connection pools**: cap the factory's Npgsql `MaxPoolSize` and raise the container's
    `max_connections`, or CI flakes `53300 too many clients`. Reuse the existing fixture.
11. **Per-storefront ledger invariants** (ADR-0045): every movement posts to the store's own derived accounts;
    this feature adds no movement, so nothing new to attribute — but the trial-balance assertion is the proof.
12. **CI gates**: `changes` / `build-test` / `integration` are required; `compose-smoke`, `kind-deploy` and
    `browser-e2e` also run. Verify locally before pushing — CI minutes are limited.
13. **To pick up new binaries locally, bounce the whole stack** (`run-all.sh stop && start`), not individual
    services.
14. **`GET /cart` returns the add-time catalog price**, not the offer price — that is why `/cart/summary`
    exists and why the storefront must never compute the promotion itself.
