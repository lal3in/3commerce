# Feature: Multi-Tenant Platform Expansion — Phase 3 Storefront/Catalog/Pricing/Payments

Parent plan: `.ai-shared/plans/multi-tenant-platform-expansion.md`

## Feature Description

Implement tenant/storefront lifecycle, upgraded product/catalog model, storefront assignment/publication, pricing/promotions, customer account scoping, payment account resolution, supplier payout instructions/payables, and Xero/accounting mappings.

## User Story

As a tenant admin
I want to configure storefronts, publish products, manage prices/promotions, payment accounts, supplier payout policies, and Xero mappings
So that each storefront can sell products with correct pricing, payment, accounting, and customer experience.

## Problem Statement

Current Catalog/Ordering/Payments assume one storefront and simple product/price/payment flows. Multi-storefront commerce needs explicit storefront assignment, publication readiness, price overrides, promotion evaluation, CheckoutAttempt before Order, payment account snapshots, and supplier payable ledger integration.

## Solution Statement

Upgrade Catalog, Ordering, Payments, and Admin/CLI in vertical slices: storefront registry/lifecycle, richer product model, pricing/promotion data, checkout snapshots, payment/Xero/payout mappings, customer account settings, and policy/legal publishing.

## Feature Metadata

**Feature Type**: Enhancement / New Capability  
**Estimated Complexity**: Very High  
**Primary Systems Affected**: Catalog, Ordering, Payments, Identity/Authz, Entity, Admin, Storefront, CLI, Workflow, Audit  
**Dependencies**: Phase 1 tenant/PDP/RLS; Phase 2 Entity/Supplier.

---

## CONTEXT REFERENCES

### Relevant Codebase Files IMPORTANT: YOU MUST READ THESE FILES BEFORE IMPLEMENTING!

- `src/Services/Catalog/Api/Endpoints/AdminEndpoints.cs` - existing product CRUD/import pattern.
- `src/Services/Catalog/Api/Endpoints/ProductsEndpoints.cs` - public product/search APIs.
- `src/Services/Ordering/Infrastructure/OrderingDbContext.cs` - cart/order/saga tables.
- `src/Services/Payments/Infrastructure/PaymentsDbContext.cs` - ledger/payments/refunds/Xero tables.
- `docs/reference/api.md` - endpoint conventions.
- `docs/reference/components.md` - Storefront/Admin UI rules.
- Phase 1 and Phase 2 plan files.

### New Files to Create

- Catalog domain files for StorefrontProduct, ProductIdentifier, ProductVariant upgrades, ProductBundle, ProductPublication, ProductSeo, Promotion definitions.
- Ordering files for CheckoutAttempt, OrderNumberSequence, pricing/tax/promotion snapshots.
- Payments files for PaymentAccount, StorefrontPaymentSettings, SupplierBankAccount, PayoutInstruction, SupplierPayablePolicy, SupplierPayable, XeroMapping.
- Admin pages/components for storefront/product/pricing/payment/Xero/payout configuration.

### Relevant Documentation YOU SHOULD READ THESE BEFORE IMPLEMENTING!

- Stripe PaymentIntents docs: https://docs.stripe.com/payments/payment-intents
- Xero API docs: https://developer.xero.com/documentation/api/accounting/overview
- Google Product structured data: https://developers.google.com/search/docs/appearance/structured-data/product

### Patterns to Follow

- Money integer minor units + ISO currency; no decimals/floats.
- Payments ledger append-only with reversing entries.
- Ordering owns final checkout totals and snapshots; Catalog provides product/price/eligibility data.
- Xero remains downstream; ledger remains source of truth.

---

## IMPLEMENTATION PLAN

### Phase 3.1: Storefront lifecycle and readiness

**Tasks:** storefront states, visibility, domains, canonical behavior, activation checks/approval.

### Phase 3.2: Product/catalog model

**Tasks:** tenant/storefront scoping, variants, identifiers, bundles, taxonomy/navigation, publication readiness.

### Phase 3.3: Pricing/promotions/tax/order snapshots

**Tasks:** SupplierCost/SellingPrice/TaxMode, storefront overrides, limited promotions, CheckoutAttempt, final revalidation, customer account scope.

### Phase 3.4: Payment/Xero/payout mapping

**Tasks:** payment accounts, supplier bank/payout hierarchy, payable policy/ledger posting, Xero mapping hierarchy.

---

## STEP-BY-STEP TASKS

### mt3_1 UPDATE storefront lifecycle, domains, readiness, activation

- **IMPLEMENT**: Draft/Preview/Active/Paused/Archived; public/private/password/invite; multiple domains one canonical; readiness checks; live-selling activation approval.
- **PATTERN**: Gateway domain context from Phase 1.
- **GOTCHA**: Paused storefront disables checkout but other tenant storefronts continue.
- **VALIDATE**: `dotnet test tests/ --filter StorefrontLifecycle`

### mt3_2 UPDATE Catalog tenant/storefront product model

- **IMPLEMENT**: TenantId/RLS; parent/variant model; identifiers; categories tenant-wide; storefront navigation; bundles/kits; structured restrictions/customs.
- **PATTERN**: `Catalog/Api/Endpoints/AdminEndpoints.cs` validation/CRUD style.
- **GOTCHA**: Product is invisible until explicitly assigned/published to storefront.
- **VALIDATE**: `dotnet test src/Services/Catalog/tests/3commerce.Catalog.Tests.csproj --filter ProductModel`

### mt3_3 ADD publication readiness and SEO/product overrides

- **IMPLEMENT**: StorefrontProduct assignment, variant visibility overrides, SEO defaults/overrides, shipping-ready checks, fulfillment source requirements.
- **PATTERN**: Catalog public `ProductsEndpoints` and Storefront component guidelines.
- **GOTCHA**: Manual/Unassigned fulfillment source blocks publication.
- **VALIDATE**: `dotnet test src/Services/Catalog/tests/3commerce.Catalog.Tests.csproj --filter Publication`

### mt3_4 ADD pricing and promotions

- **IMPLEMENT**: SupplierCost, default SellingPrice, storefront override, tax mode, one currency/storefront, limited promotions: coupon fixed/percent, automatic product/category/storefront, bundle discount, free shipping; best-discount-wins.
- **PATTERN**: money invariants in `AGENTS.md`.
- **GOTCHA**: Marketing owns campaigns, not checkout price correctness.
- **VALIDATE**: `dotnet test src/Services/Ordering/tests/3commerce.Ordering.Tests.csproj --filter Pricing`

### mt3_5 UPDATE Ordering checkout model

- **IMPLEMENT**: CheckoutAttempt before payment, final revalidation, order creation only after payment success, public order number per storefront, campaign attribution refs, customer account/storefront filtering.
- **PATTERN**: existing Ordering checkout saga tests and DbContext.
- **GOTCHA**: Failed/abandoned checkout attempts are not Orders.
- **VALIDATE**: `dotnet test src/Services/Ordering/tests/3commerce.Ordering.Tests.csproj --filter Checkout`

### mt3_6 UPDATE Payments payment account lifecycle

- **IMPLEMENT**: Tenant default + storefront override, lifecycle/readiness, provider mode snapshot, live activation approval.
- **PATTERN**: `PaymentsDbContext` and current Stripe abstraction.
- **GOTCHA**: Checkout can only use Active eligible account; payment account final snapshot before capture.
- **VALIDATE**: `dotnet test src/Services/Payments/tests/3commerce.Payments.Tests.csproj --filter PaymentAccount`

### mt3_7 ADD supplier bank accounts, payout instructions, payable policies

- **IMPLEMENT**: Payments-owned bank accounts/payout instruments, routing hierarchy, supplier payable policy configurable by tenant/supplier, ledger postings.
- **PATTERN**: ledger balance invariant tests.
- **GOTCHA**: Bank changes require approval; sensitive values masked/reveal audited.
- **VALIDATE**: `dotnet test src/Services/Payments/tests/3commerce.Payments.Tests.csproj --filter SupplierPayable`

### mt3_8 ADD Xero/accounting mappings

- **IMPLEMENT**: tenant defaults plus storefront/category/supplier/product overrides; summary journals first; detailed sync model-ready.
- **PATTERN**: existing Xero sync run/journal code.
- **GOTCHA**: Xero is downstream, never source of truth.
- **VALIDATE**: `dotnet test src/Services/Payments/tests/3commerce.Payments.Tests.csproj --filter Xero`

### mt3_9 UPDATE Admin/CLI/Storefront views

- **IMPLEMENT**: Admin CRUD for storefronts/products/prices/promotions/payment/Xero/payout; CLI commands; Storefront uses tenant/storefront product/pricing/customer context.
- **PATTERN**: Admin GatewayClient; `docs/reference/components.md`.
- **GOTCHA**: SupplierCost/margin/bank/accounting fields are permission-aware.
- **VALIDATE**: `dotnet build src/Admin/3commerce.Admin.csproj && cd src/Storefront && npm run lint && npx tsc --noEmit`

---

## TESTING STRATEGY

- Unit: product publication readiness, variant/bundle pricing, promotion best-wins, payment account resolution, payout routing.
- Integration: tenant/storefront product visibility, checkout final revalidation, payable ledger postings, Xero mapping snapshots.
- E2E: tenant admin publishes product to storefront, customer checks out, order snapshots price/payment/campaign/policy data.

## VALIDATION COMMANDS

```bash
dotnet build 3commerce.sln
dotnet test src/Services/Catalog/tests/3commerce.Catalog.Tests.csproj
dotnet test src/Services/Ordering/tests/3commerce.Ordering.Tests.csproj
dotnet test src/Services/Payments/tests/3commerce.Payments.Tests.csproj
cd src/Storefront && npm run lint && npx tsc --noEmit
```

## ACCEPTANCE CRITERIA

- [ ] Storefront lifecycle/domain/readiness implemented.
- [ ] Products support variants, identifiers, bundles, publication assignments.
- [ ] Pricing supports SupplierCost/SellingPrice/storefront override/tax mode.
- [ ] Promotions limited set works with best-discount-wins.
- [ ] CheckoutAttempt precedes Order and final revalidation happens before capture.
- [ ] Payment/Xero/payout mappings snapshot resolved values.
- [ ] Supplier payables post balanced ledger entries.

## NOTES

This phase moves several previously deferred items into target scope. Keep changes vertical and tenant-scoped; do not add cross-tenant product/supplier sharing.

---

## Addendum — storefront purchase-funnel grill (2026-06-18)

Captures the customer-facing checkout/cart/payment decisions from the design grill. Verified current-state gaps: cart lines are **product-level** (keyed by `ProductId`, priced at `MinPriceMinor`); the Ordering `ProductCopies` projection carries no variants; `User` has **no name** and `Address` has **no billing/shipping type**; checkout (`CheckoutForm.tsx`) is **guest-only** and shown to everyone; the confirmation page shows "create an account" even when authenticated; there is **no promotions or tax engine** (though `Order` already has `NetMinor/ShippingMinor/TaxMinor/GrossMinor`); shipping is a hardcoded `FlatShippingMinor = 499`; Payments has only one-off PaymentIntents (no Stripe Customer / saved cards). Quantity is already supported end-to-end (`PUT /items/{id}`, and **qty 0 already removes** the line).

### mt3_10 UPDATE cart + Ordering projection to be variant-aware

- **IMPLEMENT**: Cart line keyed by `(ProductId, VariantId)` + qty, priced at the selected variant. Extend the Catalog `ProductCopied` event and the Ordering `ProductCopies` projection to carry variant rows (sku, price, stock). Checkout/order lines and ledger reference the variant.
- **PATTERN**: `Ordering/Api/Endpoints/CartEndpoints.cs`, `CartService.cs`; Catalog parent/variant model from mt3_2.
- **GOTCHA**: Keep `qty 0 → remove`; migrate existing product-level carts gracefully.
- **VALIDATE**: `dotnet test src/Services/Ordering/tests/3commerce.Ordering.Tests.csproj --filter VariantCart`

### mt3_11 ADD customer shopping profile (name + typed address book)

- **IMPLEMENT**: Add given/family name to the customer account; give each `Address` a purpose (Billing | Shipping | Both) + `IsDefault`. Billing = residential (card AVS); shipping may differ ("send to a friend"). Saved address book in the account area. Distinct from Phase 2 Entity legal data — this is the shopper profile owned by Identity/customer scope.
- **PATTERN**: `Identity/Domain/User.cs`, `Address.cs`, `ProfileEndpoints.cs`.
- **GOTCHA**: Customer addresses are tenant/customer-scoped; do not conflate with Entity legal addresses.
- **VALIDATE**: `dotnet test src/Services/Identity/tests/3commerce.Identity.Tests.csproj --filter CustomerProfile`

### mt3_12 UPDATE checkout to be authentication-aware (single review page)

- **IMPLEMENT**: For authenticated users, prefill email/name/billing+shipping from the profile; one review page with editable addresses + send-to-friend, expandable line items with +/- that **live-recalculate** shipping/tax/discount, then Authorize & place. Hide the "create an account" block on the thank-you page for authenticated users and **auto-attach** the order to their account (no guest-convert step). Guests keep the existing convert flow.
- **PATTERN**: `Storefront/components/checkout/CheckoutForm.tsx`, `ConfirmationView.tsx`; CheckoutAttempt from mt3_5.
- **GOTCHA**: Authenticated email is server-derived, never trusted from the form.
- **VALIDATE**: `cd src/Storefront && npm run lint && npx tsc --noEmit`

### mt3_13 ADD saved cards / card-on-file (Stripe Customer + Payment Element)

- **IMPLEMENT**: Create a Stripe **Customer** per account user; Payment Element exposing **Apple Pay / Google Pay / card**; opt-in "save this card" (SetupIntent → reusable off-session PaymentMethod); saved cards listed in the account and selectable for **one-click** repeat purchase; pass the billing/residential address as card AVS. Guests pay one-off (no Customer). Keep PCI SAQ-A (card never touches our servers).
- **PATTERN**: Payments `IPaymentProvider`/Stripe adapter; payment-account lifecycle mt3_6; ADR-0014.
- **GOTCHA**: Stripe Customer id is sensitive mapping data; store in Payments only. Saved-card charges still go through the ledger/saga path.
- **VALIDATE**: `dotnet test src/Services/Payments/tests/3commerce.Payments.Tests.csproj --filter SavedCard`

### mt3_14 ADD quantity-tier promotions + DiscountMinor

- **IMPLEMENT**: Add `DiscountMinor` to order + lines; extend the promotions set (mt3_4) with **quantity-break / tiered** discounts (buy N → % or amount off) behind the promotion-engine seam; breakdown shows a discount line. Keep best-discount-wins.
- **PATTERN**: pricing/promotions mt3_4; money invariants in `AGENTS.md`.
- **GOTCHA**: Promotions affect display + checkout total; final price correctness is revalidated at checkout (mt3_5).
- **VALIDATE**: `dotnet test src/Services/Ordering/tests/3commerce.Ordering.Tests.csproj --filter Promotion`

### mt3_15 ADD ITaxStrategy seam (home regime, export zero-rating)

- **IMPLEMENT**: `ITaxStrategy` seam computing tax from a configurable home jurisdiction + rate (inclusive/exclusive), zero-rating when ship-to ≠ home (ADR-0016 DAP exports). Ships with **no home regime configured → tax = 0** (ADR-0015, no legal entity yet); breakdown always shows the tax line.
- **PATTERN**: existing `tax mode` in mt3_4; seam pattern like IPaymentProvider/ISearchProvider.
- **GOTCHA**: Multi-storefront → tax mode resolved per storefront; one currency per storefront (mt3_4).
- **VALIDATE**: `dotnet test src/Services/Ordering/tests/3commerce.Ordering.Tests.csproj --filter Tax`

### mt3_16 UPDATE storefront PDP/cart purchase UX

- **IMPLEMENT**: PDP — ensure the add-to-cart control is reliably visible, add a **variant picker** (auto-selected for single-SKU products) + **quantity stepper**, and a **shipping estimate** widget. Cart — **+/- steppers** (PUT qty; minus at 1 removes), with the shipping estimate inline. Estimate destination = **postcode + country prompt, prefilled** from the account/IP (no GPS). Calls the shipping quote API (Phase 4 mt4_10).
- **PATTERN**: `Storefront/app/products/[slug]/page.tsx`, `AddToCartButton.tsx`, `app/cart/page.tsx`, `CartItemRow.tsx`.
- **GOTCHA**: PDP/cart estimate is a "from $X" indicative quote; the exact, selectable quote is at checkout (Phase 4 mt4_5).
- **VALIDATE**: `cd src/Storefront && npm run lint && npx tsc --noEmit`

Acceptance additions:
- [ ] Cart/checkout/projection are variant-aware; qty 0 removes a line.
- [ ] Customer profile has name + typed billing/shipping address book with defaults.
- [ ] Authenticated checkout prefills and auto-attaches the order; thank-you hides "create account" for authed users.
- [ ] Saved cards (Stripe Customer + opt-in) enable one-click repeat purchase; Apple/Google Pay + card via Payment Element; PCI SAQ-A preserved.
- [ ] Quantity-tier promotions compute a DiscountMinor; ITaxStrategy zero-rates exports and defaults to 0 until a home regime is set.
- [ ] PDP/cart show variant picker, quantity steppers, and a shipping estimate.