# Feature: Project review remediation — correctness, routing, hardening, and debt

The following plan should be complete, but validate documentation and codebase patterns and task sanity before implementing. Pay special attention to naming of existing utils, types, and models. Import from the right files.

## Feature Description

A full-project review (2026-07-03, immediately after the storefront-currency/tax/admin plan shipped in PRs #36–#41) surfaced a set of findings that span four severities: **latent correctness bugs** in the money path, **incomplete wiring** of the new multi-currency/tax capability into the main shopping routes, **platform hardening** debts that keep producing the same bug class, and **documented deferrals** that need scheduling before launch. This plan captures every finding with evidence and turns them into ordered, independently shippable tasks.

## User Story

As a **platform operator preparing 3commerce for real tenants**
I want **the money path provably correct, the multi-currency capability wired into every shopper route, and the recurring bug classes eliminated at the platform level**
So that **regional storefronts can sell safely and new features stop tripping over the same enum/tax/currency landmines.**

## Problem Statement

The per-currency pricing (PR #40) and storefront tax (PR #41) capabilities are correct at the API level (live-validated) but: (a) the cart API can silently mix currencies in one cart, (b) two independent tax seams exist and would **double-tax** if Payments' `Tax:HomeCountry` were ever configured, (c) the shipped endpoints have **zero automated test coverage** and stale OpenAPI contracts, and (d) the main shopper routes (`/products/[slug]`, `/search`, `/cart`, add-to-cart) never pass a currency or storefront context, so the flagship capability is only reachable on the `/{storefront}` demo landing pages. Separately, the enum-as-string bug class has now caused five distinct 500s across seed + admin, and RLS/authz follow-ups recorded in the tracker remain open.

## Solution Statement

Fix the two money-path bugs and close the test/contract gap first (P1). Then wire the existing gateway `DomainResolutionMiddleware` headers through the storefront so every route is storefront-scoped (P2). Kill the enum bug class and 500-on-bad-body behavior platform-wide, and expand RLS per the tracker follow-ups (P3). Write the ADR reconciling `VariantPrice` with ADR-0028's Offer price ownership (P4). Fix the broken `json_get` seed helper and add currency/tax e2e coverage (P5). Triage the documented capability deferrals into a scheduled backlog (P6).

## Feature Metadata

**Feature Type**: Bug Fix + Enhancement + Refactor (mixed)
**Estimated Complexity**: Medium overall — P1 tasks are small and surgical; P2 (routing) is the largest single piece
**Primary Systems Affected**: Ordering (cart/checkout/tax), Payments (tax seam), Catalog (OpenAPI/tests), Storefront (routing/context), Gateway (existing middleware reuse), Identity/Entity (RLS/authz), scripts (seed), docs (ADR/OpenAPI)
**Dependencies**: None new. Everything builds on merged main (through PR #41).

---

## FINDINGS REGISTER (evidence)

| # | Severity | Finding | Evidence |
|---|----------|---------|----------|
| F1 | **High (latent bug)** | **Mixed-currency cart**: `AddItem` prices per-request currency but never checks existing items' currency. A cart can hold AUD + EUR lines; checkout then uses `cart.Items[0].Currency` and sums ALL unit prices regardless of currency → wrong total charged in the wrong currency. | `CartEndpoints.cs:76-99` (no guard), `CheckoutEndpoints.cs:63-64` (`items[0].Currency` + blind sum) |
| F2 | **High (latent bug)** | **Double-tax seam**: Ordering now computes + charges storefront tax (PR #41), but Payments' `AuthorizePaymentConsumer` still applies `ITaxStrategy.TaxFor(msg.NetMinor…)` on top of the already-tax-inclusive net. Dev is safe only because `Tax:HomeCountry` is unset (rate 0). Configure it and every order is taxed twice; `grossMinor` authority is also split across services. | `AuthorizePaymentConsumer.cs:39-40` (`taxMinor = tax.TaxFor(msg.NetMinor…)`, `grossMinor = NetMinor + taxMinor`), vs `CheckoutEndpoints.cs:85-92` (Ordering tax already inside `netMinor`) |
| F3 | **High (coverage gap)** | **Zero automated tests for per-currency pricing and storefront tax.** No unit/integration/e2e file references `VariantPrice`, `StorefrontTaxCopy`, `currency=` search, or `taxRateBasisPoints`. PRs #40/#41 were validated live only; a regression would ship silently. | `grep -rln` across `tests/`, `src/Services/*/tests`, `src/Storefront/e2e*` → no matches |
| F4 | **Medium (compliance)** | **OpenAPI/contract drift**: `catalog.openapi.json` last regenerated at #27 (misses PR #39 public storefront endpoint + PR #40 `?currency` params + `VariantPrice` DTOs); `ordering.openapi.json` last at #29 (misses cart `currency` field, checkout tax). Violates the AGENTS.md regenerate-after-endpoint-change rule; `api_contracts_index.md` likewise. | `git log -- docs/api/*.openapi.json` |
| F5 | **High (product gap)** | **Main shopper routes not storefront-scoped**: `AddToCartButton` never passes currency; `/products/[slug]`, `/search`, `/cart`, `/checkout` all run in base EUR; nothing sends `X-3C-Tenant-Id`/`X-3C-Storefront-Id`, so `CheckoutAttempt.StorefrontId` falls back to the tenant id — AU/EU/US orders are mis-attributed. **The gateway already resolves host→tenant/storefront headers** (`DomainResolutionMiddleware`, mt1_5) — this is a wiring gap, not missing infra. | `AddToCartButton.tsx:75`, `CheckoutEndpoints.cs:120-121`, `Gateway/Tenancy/DomainResolutionMiddleware.cs:14-15` |
| F6 | **Medium (legal/UX)** | **Tax display convention**: PR #41 charges exclusive-add for ALL regimes. AU GST and EU VAT are legally **tax-inclusive display** markets (shelf price must include tax); only US sales tax is conventionally added at checkout. Also `formatMoney` renders AUD as bare `$` (ambiguous vs USD). | `CheckoutEndpoints.cs:85-92` (exclusive for all), `lib/money.ts` |
| F7 | **Medium (recurring bug class)** | **Enum-as-string over JSON**: five distinct 500s so far (seed: entity/pricing/location/carrier/address; admin: payment-account mode). Root cause is minimal-API numeric enum binding + `TreatWarningsAsErrors`-style silence. No platform decision exists (JsonStringEnumConverter everywhere vs numeric-everywhere), and malformed bodies surface as **500** (`BadHttpRequestException` unhandled) instead of 400. | identity/entity/pricing/payments logs from this session; `ProfileEndpoints`, `PaymentAccountAdminEndpoints` |
| F8 | **Medium (security posture)** | **RLS/authz follow-ups open** (recorded in tracker, unscheduled): only Identity `Users`/`ServiceAccounts` + `entity.Entities` FORCE RLS; Identity `Addresses` deferred; other Entity tenant tables un-RLS'd; Catalog/Ordering/Payments/Fulfillment/Support rely on app-level filtering (accepted, but undocumented as a posture decision); cross-tenant master-admin user mgmt should require a platform-scope `MasterGlobal` grant (aui_8 note). | tracker rows mt1_4, aui_8, aui_9, mt4_1 |
| F9 | **Medium (architecture)** | **Price-ownership divergence**: ADR-0028 makes the **Offer** the price owner and mt7_1 carries a "retire `Variant.PriceMinor`" follow-up — but per-currency pricing (PR #40) deepened investment in Variant-based pricing (`VariantPrice`). Offers have one flat currency price. Two price systems now coexist with no reconciling decision; no ADR was written for per-currency pricing. | `docs/adr/adr_index.md` (no per-currency ADR), tracker mt4_1b/mt7_1 FOLLOW-UPs |
| F10 | **Low (dev hygiene)** | **`json_get` in `dev-dummy-data.sh` is fully broken** (heredoc consumes python stdin → always returns empty): manifest product ids never captured, entity linkage falls back to a placeholder, scenario matrix degrades, checkout scenarios skip. Fixing it will also **unmask** remaining string-enum seed payloads (e.g. `entity-change-request` sends `type:"Contact"`). | `dev-dummy-data.sh:137-155`; manifest `warnings` array from live runs |
| F11 | **Low (known infra)** | Testcontainers integration suite is flaky under full-parallel load (documented in mt6_12 as resource contention); browser-e2e retains `retries:2` as a safety net; MCR registry outages block non-required docker CI jobs. | tracker mt6_12; this session's #41 merge history |
| F12 | **Backlog (documented deferrals)** | Capability-first primitives shipped without service wiring, all explicitly deferred in the tracker: CLI real auth (mt1_6), analytics events endpoint (mt5_5), publishing persistence/endpoints (mt5_7), MFA factors + enrollment (mt6_10), per-provider webhook secret registry (mt6_7), mission-control live bus stats (mt6_14), real Stripe/Xero/carrier credential swaps, external pen test (PRD Appendix B launch gates). | tracker rows cited per item |

---

## CONTEXT REFERENCES

### Relevant Codebase Files — READ BEFORE IMPLEMENTING

- `src/Services/Ordering/Api/Endpoints/CartEndpoints.cs` (AddItem lines 59-105; `SelectVariant`; cart response currency line 192) — Why: F1 guard goes here.
- `src/Services/Ordering/Api/Endpoints/CheckoutEndpoints.cs` (lines 63-92 tax block; 120-121 header fallback; 130-150 attempt snapshot) — Why: F1/F2/F5 touch points.
- `src/Services/Payments/Infrastructure/Consumers/AuthorizePaymentConsumer.cs` (lines 16, 39-40, 65, 75) — Why: F2 — the seam to neutralize/remove.
- `src/Services/Payments/Infrastructure/FlatRateTaxStrategy.cs` + `src/Services/Payments/Domain/ITaxStrategy.cs` — Why: F2 — decide delete vs keep-as-explicit-zero.
- `src/BuildingBlocks/Contracts/Payments/*.cs` (`AuthorizePayment`/`AuthorizePaymentResult`) — Why: F2 — gross/tax fields flow through the saga (`CartSubmitted` uses `intent.GrossMinor`).
- `src/Gateway/Tenancy/DomainResolutionMiddleware.cs` (headers at 14-15) — Why: F5 — existing host→context resolution to reuse, config-backed host map.
- `src/Storefront/components/cart/AddToCartButton.tsx` (line 75), `src/Storefront/lib/cart-actions.ts` (`addToCart` currency param exists already), `src/Storefront/lib/gateway.ts` (`getStorefrontConfig`, `searchProducts(currency)`, `getProduct(slug, currency)`) — Why: F5 — plumbing already exists API-side; the UI never passes it.
- `src/Storefront/app/products/[slug]/page.tsx`, `app/search/page.tsx`, `app/cart/page.tsx` — Why: F5 — routes to make storefront-context-aware.
- `src/Storefront/lib/money.ts` (`formatMoney`) — Why: F6 — currency symbol disambiguation (locale or `currencyDisplay`).
- `src/Services/Catalog/Domain/VariantPrice.cs`, `src/Services/Catalog/Infrastructure/Search/PostgresSearchProvider.cs` (per-currency SQL), `src/Services/Ordering/Domain/ProductCopy.cs` (`PriceInCurrency`), `src/Services/Ordering/Domain/StorefrontTaxCopy.cs`, `src/Services/Ordering/Infrastructure/Projections/StorefrontConfigConsumer.cs` — Why: F3 — the untested surfaces.
- `tests/3commerce.IntegrationTests/MoneyFlowTests.cs` + `Phase3Fixture.cs` — Why: F3 — the fixture/pattern for checkout-path integration tests (`MintInternalClaims`, `[Collection]`).
- `src/Services/Identity/Api/Endpoints/AdminUsersEndpoints.cs` — Why: F8 — `MasterGlobal` gate goes here.
- `src/Services/Entity/Infrastructure/Migrations/20260626233320_FixEntityRlsNullifGuard.cs` + `src/BuildingBlocks/Infrastructure/Tenancy/TenantScopeMiddleware.cs` — Why: F8 — the established RLS policy + middleware pattern to extend.
- `scripts/dev-dummy-data.sh` (`json_get` lines 137-155; change-request payload ~line 499) — Why: F10.
- `docs/adr/adr_index.md`, `docs/adr/0028-*.md` — Why: F9 — ADR to add + reconcile.
- `src/Storefront/e2e-admin/admin-actions.spec.ts`, `src/Storefront/e2e-supplier/supplier.spec.ts` — Why: F3/F11 — established Playwright patterns (`toPass`, circuit handling).

### New Files to Create

- `src/Services/Catalog/tests/VariantPriceTests.cs` — unit: per-currency add/replace/normalize invariants.
- `tests/3commerce.IntegrationTests/CurrencyPricingTests.cs` — integration: search `?currency`, hide-missing, cart-add currency, mixed-currency rejection.
- `tests/3commerce.IntegrationTests/StorefrontTaxTests.cs` — integration: `StorefrontConfigChanged` → `StorefrontTaxCopy` projection; AUD/EUR/USD checkout tax math; no-double-tax assertion.
- `docs/adr/00XX-per-currency-pricing-ownership.md` — F9 decision record.
- `src/Storefront/lib/storefront-context.ts` (or extend `lib/storefront.ts` naming from the prior plan) — resolve active storefront once per request for main routes.

### Patterns to Follow

- **Money**: integer minor units + ISO 4217; never floats (AGENTS.md invariant).
- **Cross-service data**: project via bus events into local read copies (ADR-0008) — never query another service. `StorefrontConfigConsumer` is the fresh example.
- **Outbox**: `publisher.Publish(...)` **before** `SaveChangesAsync` or the message never flushes (bug class hit in PR #41 — comment exists at `StorefrontEndpoints.PublishConfigAsync` call sites).
- **Admin action feedback**: `_error/_status` set from response; set success message **after** any reload that clears it (PR #36 bug class).
- **Playwright + Blazor**: fill+submit+assert as one `expect(...).toPass()` unit; prerender is off in admin so first-click works.
- **Tracker rule**: update `.ai-shared/plans/plan_status_executions.md` in the SAME change as the work; no side follow-up docs.

---

## IMPLEMENTATION PLAN

Ordered by risk. Each phase = one PR unless noted. Standard gates every PR: `dotnet build` 0/0, `dotnet format`, storefront `lint`+`tsc` when touched, unit+integration, e2e where relevant, tracker updated in-change, OpenAPI regenerated when endpoints change, merge on green. No AI/Co-Authored-By trailers.

### Phase 1 — Money-path correctness + coverage (PR A, PR B)

**PR A (bugs):**
- **rev_1 — Mixed-currency cart guard (F1).** In `CartEndpoints.AddItem`, after resolving `currency`: if the cart has items and `cart.Items[0].Currency != currency`, return 409 Conflict ("Cart is in {X}; empty it to shop in {Y}") — or auto-key carts per currency if product prefers. Simplest correct v1: reject. Also make checkout defensively assert single-currency (400 if mixed — data from before the guard).
- **rev_2 — Single tax owner (F2).** Ordering is the tax authority (decided in PR #41). Remove the `ITaxStrategy` application from `AuthorizePaymentConsumer` (charge exactly `msg.NetMinor`; `taxMinor = 0` in the intent or drop the field through the contract with all consumers updated in the same PR — check `AuthorizePaymentResult`/`CartSubmitted` usages). Delete or quarantine `FlatRateTaxStrategy` + `Tax:HomeCountry`/`Tax:FlatRate` config keys and their doc references so they can't be re-enabled into a double-tax. Update the Phase-3 tax-seam note in the tracker.

**PR B (coverage/compliance):**
- **rev_3 — Regenerate OpenAPI + contract index (F4).** Boot Catalog + Ordering in Development, `curl :510x/openapi/v1.json` → `docs/api/catalog.openapi.json` + `ordering.openapi.json`; update `api_contracts_index.md` (public storefronts endpoint, `?currency` params, cart `currency`, checkout tax semantics).
- **rev_4 — Automated tests for currency + tax (F3).** Files listed above. Minimum: VariantPrice normalize/unique unit tests; integration — `?currency=AUD` returns AUD + hides unpriced, JPY hides all; cart add with currency + mixed-currency 409 (guards rev_1); `StorefrontConfigChanged` projection upsert; checkout AUD=10%/EUR=20%/USD=8.25% math incl. rounding (`MidpointRounding.AwayFromZero`); a no-double-tax assertion pinning rev_2 (gross == net computed by Ordering).

### Phase 2 — Storefront context on every route (PR C, PR D)

- **rev_5 — Host→storefront wiring (F5).** (largest task) Storefront resolves its active storefront once per request: production = the gateway's `DomainResolutionMiddleware` host map (register storefront domains); local dev = path-slug or an env override (`STOREFRONT_SLUG`). Thread the resolved context: `searchProducts`/`getProduct`/`AddToCartButton→addToCart` pass `currency`; cart/checkout server actions forward `X-3C-Tenant-Id`/`X-3C-Storefront-Id` so `CheckoutAttempt.StorefrontId` attributes correctly. Decide the local-dev UX explicitly (likely: `/{storefront}/products/...` route group OR cookie set when entering `/{slug}`). Validate: order placed from `/au` lands with the AU `StorefrontId` and AUD lines.
- **rev_6 — Tax display convention + currency symbols (F6).** Per-regime display rule: AuGst/EuVat → tax-INCLUSIVE shelf prices ("incl. GST/VAT" label at checkout, tax line shown as informational "includes X"), UsSalesTax → exclusive add (current behavior). Requires deciding whether tenant per-currency prices are entered inclusive or exclusive per regime — document in the ADR (rev_10) and keep charge math consistent with display. `formatMoney`: pass an explicit locale or `currencyDisplay: "narrowSymbol"`→disambiguate (A$ vs $) — verify AUD/USD/EUR rendering snapshots.

### Phase 3 — Platform hardening (PR E, PR F)

- **rev_7 — Enum + bad-body policy (F7).** Decide once: adopt `JsonStringEnumConverter` in every service's JSON options (accepts both numbers and names — kills the class; check bus-serialization is unaffected since MassTransit has its own settings) OR mandate numeric-only and lint for it. Either way, add exception handling so `BadHttpRequestException`/`JsonException` body-binding failures return **400** problem-details, not 500 (one `AddApiProblemDetails` extension change in BuildingBlocks.Web covers all services). Update AGENTS.md with the decision.
- **rev_8 — RLS expansion + posture doc (F8).** Extend FORCE RLS + `TenantScopeMiddleware` coverage: Identity `Addresses` (mt1_4 deferral, with the dedicated policy test), remaining `entity.*` tenant tables (aui_9 note). Write the posture decision for app-level-filtered services (Catalog/Ordering/Payments/Fulfillment/Support) into an ADR or ADR-0024 addendum so it's a choice, not an accident. Non-superuser regression tests per the `EntityRlsTests` pattern.
- **rev_9 — MasterGlobal gate for cross-tenant admin (F8/aui_8).** Add platform-scope authorization to `AdminUsersEndpoints` (and any cross-tenant admin surface) so tenant admins can't manage other tenants' users by passing a foreign `tenantId`.

### Phase 4 — Pricing architecture decision (PR G, docs-first)

- **rev_10 — ADR: per-currency price ownership (F9).** Reconcile: Variant carries the tenant's **shelf price per currency** (display + cart), Offer carries **supply/billing pricing** (supplier cost, models, tiers — ADR-0028) — or fold per-currency into Offers and retire `Variant.PriceMinor` (the standing mt7_1 follow-up). Record inclusive/exclusive-per-regime price entry (rev_6 dependency). Outcome: one ADR + adr_index row + updated mt7_1 follow-up wording; implementation of whichever migration follows as its own scheduled task.

### Phase 5 — Dev/QA hygiene (PR H)

- **rev_11 — Fix `json_get` + unmasked seed payloads (F10).** Replace the heredoc-stdin `json_get` with `python3 -c` (stdin free for the pipe; the inline reader in `upsert_demo_storefront`/pricing lookup is the working pattern — consolidate on it). Then run `--profile full` and fix every newly-unmasked step (known: `entity-change-request` `type:"Contact"` → numeric; audit remaining `allowed_4xx` for provider/scope strings in payment-account/supplier-bank/xero steps). Success bar: manifest captures real product/entity ids, checkout scenarios stop skipping, `server_error=0`, warnings list shrinks to intentional ones.
- **rev_12 — e2e for currency/tax (F3/F11).** Playwright: `/au` grid shows AUD price (assert the actual seeded value), checkout on AU shows the GST line matching the charge; keep `retries:2` as the safety net. Note CI Testcontainers full-parallel flake as a resourcing item (no code change; document threshold in e2e-verify).

### Phase 6 — Deferred-capability triage (no code in this plan)

- **rev_13 — Schedule the documented deferrals (F12).** One triage pass producing dated tracker entries (or explicit "post-launch" tags) for: CLI real auth (mt1_6), analytics events endpoint (mt5_5), publishing persistence (mt5_7), MFA enrollment (mt6_10), webhook secret registry (mt6_7), mission-control live stats (mt6_14), real Stripe/Xero/carrier credential swaps, external pen test. Output is a decision per item — schedule, park, or drop — not implementations.

---

## STEP-BY-STEP TASKS

### rev_1 UPDATE `src/Services/Ordering/Api/Endpoints/CartEndpoints.cs`
- **IMPLEMENT**: currency-mismatch guard in `AddItem` (409 + message); defensive single-currency assert in `CheckoutEndpoints.Checkout` (400).
- **PATTERN**: existing `TypedResults.NotFound($"…not available in {currency}.")` style at CartEndpoints.cs:80.
- **GOTCHA**: guest cart merge on login (`CartService`) — verify merge can't combine two currencies either.
- **VALIDATE**: `dotnet test tests/3commerce.IntegrationTests --filter CurrencyPricing` (new test from rev_4 red→green).

### rev_2 UPDATE `src/Services/Payments/Infrastructure/Consumers/AuthorizePaymentConsumer.cs` (+ contract + config + docs)
- **IMPLEMENT**: charge `msg.NetMinor` verbatim; remove `ITaxStrategy` from the consumer; delete/quarantine `FlatRateTaxStrategy` and `Tax:*` keys; sweep `AuthorizePaymentResult.TaxMinor` consumers (CheckoutEndpoints no longer uses it — confirm nothing else does) and either zero it or remove it with ALL consumers in one PR (bus contract!).
- **GOTCHA**: shared bus contract — same-PR rule as `ProductUpserted` (PR #40 precedent).
- **VALIDATE**: MoneyFlow + new StorefrontTax integration tests green; `grep -rn "Tax:HomeCountry"` returns only docs/history.

### rev_3 REGENERATE `docs/api/catalog.openapi.json`, `docs/api/ordering.openapi.json`, UPDATE `docs/api/api_contracts_index.md`
- **VALIDATE**: `git diff docs/api` shows the new endpoints/params; index rows mention `?currency` + public storefront config + checkout tax.

### rev_4 CREATE test files (listed in New Files)
- **PATTERN**: `Phase3Fixture` + `[Collection]` for Ordering+Payments; `EntityRlsTests` style for projections; Catalog unit tests colocated in `src/Services/Catalog/tests`.
- **VALIDATE**: `dotnet test tests/3commerce.IntegrationTests --filter "CurrencyPricing|StorefrontTax"` and `dotnet test src/Services/Catalog/tests --filter VariantPrice`.

### rev_5 UPDATE storefront routing/context (largest)
- **IMPLEMENT**: per-request storefront resolution (host in prod via gateway headers; slug/env in dev); pass currency into `searchProducts`/`getProduct`/`addToCart`; forward `X-3C-Tenant-Id`/`X-3C-Storefront-Id` on checkout POST.
- **GOTCHA**: `getProduct` uses ISR (`revalidate: 300`) — the URL carries `?currency` so cache keys split correctly; keep it that way.
- **VALIDATE**: live — order placed from `/au` has AU `StorefrontId` + AUD lines end-to-end (`ordering."CheckoutAttempts"` row).

### rev_6 UPDATE tax display + `formatMoney`
- **IMPLEMENT**: per-regime inclusive/exclusive display; A$/US$ disambiguation.
- **GOTCHA**: display change must not silently change the charged amount — charge math changes only if the ADR (rev_10) says prices are entered inclusive.
- **VALIDATE**: Playwright currency/tax spec (rev_12) asserts the label + amount.

### rev_7 UPDATE BuildingBlocks.Web JSON/problem-details (+ AGENTS.md)
- **VALIDATE**: posting `{"mode":"Test"}` to payment-accounts returns 400 problem-details (or 201 if converter adopted), never 500. Add one integration test asserting the status class.

### rev_8 ADD RLS migrations + policies (Identity Addresses, remaining entity tables) + posture ADR note
- **PATTERN**: `FixEntityRlsNullifGuard` policy shape; `TenantScopeMiddleware<TDbContext>`; non-superuser test role per `EntityRlsTests`.
- **VALIDATE**: new `*RlsTests` green as non-superuser; full integration suite green.

### rev_9 UPDATE `AdminUsersEndpoints` with platform-scope gate
- **VALIDATE**: tenant-scoped admin token + foreign tenantId → 403 (new integration test).

### rev_10 CREATE per-currency pricing ADR + adr_index row
- **VALIDATE**: ADR states Variant-vs-Offer ownership, inclusive/exclusive entry rule, and the fate of mt7_1's "retire Variant.PriceMinor".

### rev_11 UPDATE `scripts/dev-dummy-data.sh` (`json_get` + unmasked payloads)
- **VALIDATE**: `bash -n`; `--profile full` → `server_error=0`, manifest has real ids, checkout scenarios run (warnings shrink).

### rev_12 CREATE storefront currency/tax Playwright spec
- **VALIDATE**: `npx playwright test --project=storefront` green against `dev-up --fresh --data full`.

### rev_13 TRIAGE deferrals → tracker updates only
- **VALIDATE**: every F12 item has a dated decision row.

---

## TESTING STRATEGY

**Unit**: VariantPrice invariants; tax rounding math; formatMoney symbol snapshots.
**Integration**: currency search/hide/cart/mixed-409; StorefrontConfigChanged projection; AUD/EUR/USD checkout tax; no-double-tax pin; enum-bad-body → 4xx; RLS non-superuser suites; MasterGlobal 403.
**E2E**: `/au` price + GST label/amount; admin per-currency price row editing (extend `admin-actions.spec.ts`).
**Edge cases**: cart merge-on-login across currencies; storefront currency changed after items carted (revalidation 409 path); tax bps = 0 regime; rounding at .5 minor unit; JPY-style unpriced currency everywhere.

## VALIDATION COMMANDS

- Level 1: `dotnet format --verify-no-changes` · `cd src/Storefront && npm run lint && npx tsc --noEmit` · `bash -n scripts/dev-dummy-data.sh`
- Level 2: `dotnet test src/Services/Catalog/tests` · `dotnet test src/Services/Ordering/tests` · `dotnet test src/Services/Payments/tests`
- Level 3: `dotnet test tests/3commerce.IntegrationTests` · `cd src/Storefront && npx playwright test`
- Level 4 (manual, against `scripts/dev-up.sh --fresh --with-frontends --data full`): mixed-currency add → 409; AU checkout: GST line, AUD `StorefrontId`; `Tax:HomeCountry` grep clean; OpenAPI diff reviewed.
- Level 5: `scripts/e2e-verify.sh`

## ACCEPTANCE CRITERIA

- [ ] A cart can never contain two currencies; checkout rejects legacy mixed data.
- [ ] Exactly one tax computation exists (Ordering); configuring legacy `Tax:*` keys is impossible or inert; gross authority is unambiguous.
- [ ] Per-currency pricing + storefront tax have unit, integration, and e2e coverage that fails on regression.
- [ ] OpenAPI + contract index reflect every shipped endpoint (PRs #39–#41).
- [ ] Every main shopper route operates in the active storefront's currency; orders attribute the correct `StorefrontId`.
- [ ] AU/EU display tax-inclusive per convention (per ADR decision); AUD/USD symbols unambiguous.
- [ ] Malformed request bodies return 4xx problem-details platform-wide; the enum policy is written into AGENTS.md.
- [ ] RLS coverage matches the tracker follow-ups; app-level-filter posture is a recorded decision.
- [ ] Per-currency price ownership ADR merged; mt7_1 follow-up updated.
- [ ] `--profile full` seed: `server_error=0`, real manifest ids, checkout scenarios execute.
- [ ] Every F12 deferral has a dated schedule/park/drop decision in the tracker.

## COMPLETION CHECKLIST

- [ ] PRs A–H merged on green in phase order (P1 before P2; rev_10 ADR before/with rev_6 charge-math changes).
- [ ] Tracker rows rev_1..rev_13 updated in the same change as each task.
- [ ] No regressions: full `e2e-verify.sh` pass at the end.

## NOTES

- **Priority rationale**: F1/F2 are latent money bugs — cheap to fix now, expensive after real tenants configure things. F3/F4 make the flagship feature safe to evolve. F5 is the biggest user-visible gap (the capability exists but only demo pages use it). F7 has already cost five debugging cycles this month.
- **Sequencing dependency**: rev_6 (inclusive display) must not change charge math until rev_10's ADR decides whether tenant prices are entered inclusive or exclusive per regime. Ship rev_6 display-labeling first if the ADR stalls.
- **Known non-goals**: real Stripe/Xero/carrier integrations, Kafka lane, dedicated Pricing/Entitlement services — all remain capability-first deferrals per ADR-0028/0029 and are covered only by rev_13 triage.
- **Confidence score**: 8/10 for one-pass success on rev_1–4, 7, 9–13 (small, well-anchored); 6/10 on rev_5 (routing UX decisions) and rev_8 (RLS migrations on live tables need care); rev_6 gated on rev_10.
