# Feature: Multi-Tenant Platform Expansion — Phase 4 Shipping/Inventory/Fulfillment

Parent plan: `.ai-shared/plans/multi-tenant-platform-expansion.md`

## Feature Description

Implement fulfillment-source-aware inventory, live carrier quotes/labels/tracking for Australia Post and DHL, multi-shipment/multi-package orders, manual label/tracking fallback, shipment-aware RMA/support, and order holds.

## User Story

As a tenant operations/admin user
I want live shipping quotes, inventory reservations, package/label/tracking management, and partial fulfillment
So that orders can be fulfilled accurately across warehouses, suppliers, and forwarders.

## Problem Statement

Current fulfillment is basic and does not support live carrier quotes, package labels, tracking automation, inventory reservations, or multi-origin carts. With multi-storefront physical commerce, checkout must guarantee valid shipping methods/prices before payment and fulfillment must handle split shipments.

## Solution Statement

Make Fulfillment the owner of inventory/reservations, carrier integrations, locations, quotes, labels, tracking, shipments, packages, and fulfillment allocations. Ordering requests quote/revalidation/reservation but does not call carriers directly.

## Feature Metadata

**Feature Type**: Enhancement / New Capability  
**Estimated Complexity**: Very High  
**Primary Systems Affected**: Fulfillment, Ordering, Catalog, Entity, Payments, Support, Admin, Supplier Portal, Gateway, Workflow, Audit  
**Dependencies**: Phase 1 tenant/RLS/PDP, Phase 2 Entity, Phase 3 checkout/product publication.

---

## CONTEXT REFERENCES

### Relevant Codebase Files IMPORTANT: YOU MUST READ THESE FILES BEFORE IMPLEMENTING!

- `src/Services/Fulfillment/Api/Endpoints/AdminShipmentsEndpoints.cs` - current shipment endpoint pattern.
- `src/Services/Ordering/Infrastructure/OrderingDbContext.cs` - checkout/order saga context.
- `src/Services/Catalog/Api/Endpoints/AdminEndpoints.cs` - product stock fields to migrate/project.
- Phase 1-3 plan files.
- `docs/reference/api.md` and `AGENTS.md`.

### New Files to Create

- Fulfillment domain: InventoryLocation, InventoryItem, InventoryReservation, CarrierIntegration, CarrierQuote, Shipment, Package, FulfillmentAllocation.
- Carrier adapter abstractions: `ICarrierRateProvider`, `ICarrierLabelProvider`, `ICarrierTrackingProvider`.
- Fake/test carrier, Australia Post adapter, DHL adapter.
- Admin fulfillment/carrier/inventory pages.

### Relevant Documentation YOU SHOULD READ THESE BEFORE IMPLEMENTING!

- Australia Post Shipping and Tracking API: https://developers.auspost.com.au/apis/shipping-and-tracking/reference
- DHL MyDHL API: https://developer.dhl.com/api-reference/mydhl-api
- OWASP File Upload Cheat Sheet: https://cheatsheetseries.owasp.org/cheatsheets/File_Upload_Cheat_Sheet.html

### Patterns to Follow

- Carrier credentials via secret-store abstraction; service stores CredentialRef.
- Fulfillment owns inventory/reservations; Catalog receives availability projections.
- Ordering requests quotes/reservations/revalidation; Fulfillment owns carrier calls.
- Use fake carrier in tests and local dev.

---

## IMPLEMENTATION PLAN

### Phase 4.1: Inventory and locations

**Tasks:** operational locations linked to Entity, inventory by source/location, supplier stock feeds, reservations.

### Phase 4.2: Carrier integrations

**Tasks:** Australia Post/DHL/fake carriers, tenant default + storefront override, lifecycle/readiness, credentials, modes.

### Phase 4.3: Checkout shipping

**Tasks:** shipment groups, live quotes, quote expiry/revalidation, fallback policies, combined shipping total.

### Phase 4.4: Fulfillment execution

**Tasks:** packages, labels/tracking toggles, manual labels/tracking, partial fulfillment, order holds, RMA support.

---

## STEP-BY-STEP TASKS

### mt4_1 UPDATE Fulfillment inventory locations and stock

- **IMPLEMENT**: InventoryLocation linked to Entity/address; InventoryItem by product/variant/fulfillment source; supplier stock feed updates.
- **PATTERN**: existing Fulfillment service structure and EF DbContext conventions.
- **GOTCHA**: Supplier users can update stock feeds but not dispatch/tracking v1.
- **VALIDATE**: `dotnet test src/Services/Fulfillment/tests/3commerce.Fulfillment.Tests.csproj --filter Inventory`

### mt4_2 ADD hybrid inventory reservations

- **IMPLEMENT**: availability check in cart/checkout, hard reservation during checkout/payment saga, timeout release, commit on payment success.
- **PATTERN**: Ordering saga and MassTransit timeout patterns.
- **GOTCHA**: Supplier feed updates must not overwrite active reservations incorrectly.
- **VALIDATE**: `dotnet test tests/ --filter InventoryReservation`

### mt4_3 ADD carrier integration model and lifecycle

- **IMPLEMENT**: CarrierIntegration tenant default + storefront override, provider type, mode, credential ref, QuotesEnabled, LabelCreationEnabled default off, TrackingSyncEnabled default off, readiness states.
- **PATTERN**: payment account lifecycle from Phase 3.
- **GOTCHA**: Live mode activation requires approval; non-production cannot use live credentials.
- **VALIDATE**: `dotnet test src/Services/Fulfillment/tests/3commerce.Fulfillment.Tests.csproj --filter CarrierIntegration`

### mt4_4 ADD carrier adapters: fake, Australia Post, DHL

- **IMPLEMENT**: Rate/label/tracking abstractions and adapters; fake carrier deterministic for tests.
- **PATTERN**: existing `IPaymentProvider`/`ISearchProvider` seam pattern.
- **GOTCHA**: Australia Post/DHL real account/API contract access may be external launch/onboarding dependency.
- **VALIDATE**: `dotnet test src/Services/Fulfillment/tests/3commerce.Fulfillment.Tests.csproj --filter CarrierAdapter`

### mt4_5 UPDATE checkout shipping groups and quote selection

- **IMPLEMENT**: Fulfillment source grouping, per-group quote lists, customer selects one method per group, combined shipping total.
- **PATTERN**: Ordering checkout endpoint/saga patterns.
- **GOTCHA**: One order can have multiple shipments; customer still completes one checkout.
- **VALIDATE**: `dotnet test tests/ --filter MultiShipmentCheckout`

### mt4_6 ADD quote expiry, fallback, and final revalidation

- **IMPLEMENT**: provider expiry else configured default; revalidate before payment; fallback policies: cached/manual/hide/block.
- **PATTERN**: Checkout final revalidation from Phase 3.
- **GOTCHA**: Customer must reconfirm material shipping price changes.
- **VALIDATE**: `dotnet test tests/ --filter ShippingQuoteRevalidation`

### mt4_7 ADD shipments, packages, labels, tracking

- **IMPLEMENT**: multi-package shipments, package item allocations, label refs/files, tracking per package/shipment, manual upload/record source.
- **PATTERN**: current Fulfillment shipments and object storage plan.
- **GOTCHA**: When automation off, admins may upload labels/record external tracking; avoid duplicate labels when later enabled.
- **VALIDATE**: `dotnet test src/Services/Fulfillment/tests/3commerce.Fulfillment.Tests.csproj --filter ShipmentPackage`

### mt4_8 UPDATE Support/RMA for shipment/order-line awareness

- **IMPLEMENT**: line/shipment/package-aware RMA, manual inspection/restock, supplier payable adjustments, support ticket links.
- **PATTERN**: existing per-line RMA implementation noted in status BL-8.
- **GOTCHA**: Refund path remains Support -> Payments saga only.
- **VALIDATE**: `dotnet test src/Services/Support/tests/3commerce.Support.Tests.csproj --filter Rma`

### mt4_9 ADD order holds before fulfillment

- **IMPLEMENT**: fraud/payment/address/inventory/supplier/compliance holds; release/cancel with reason/permission.
- **PATTERN**: saga state conflict handling and audit requirements.
- **GOTCHA**: Held orders must not proceed to fulfillment automation.
- **VALIDATE**: `dotnet test tests/ --filter OrderHold`

---

## TESTING STRATEGY

- Unit: carrier fallback policy, quote expiry, inventory reservation math, package allocation.
- Integration: checkout quote/revalidate/payment; inventory commit/release; label/tracking flows; partial fulfillment.
- E2E: product from two fulfillment sources, select two shipping methods, one payment, two shipments.

## VALIDATION COMMANDS

```bash
dotnet build 3commerce.sln
dotnet test src/Services/Fulfillment/tests/3commerce.Fulfillment.Tests.csproj
dotnet test src/Services/Support/tests/3commerce.Support.Tests.csproj
dotnet test tests/ --filter Fulfillment
```

## ACCEPTANCE CRITERIA

- [ ] Fulfillment owns inventory/reservations and operational locations.
- [ ] Australia Post, DHL, and fake carrier adapter model exists.
- [ ] Checkout uses live quote selection per shipment group and revalidates before payment.
- [ ] Shipments support multiple packages and split line quantities.
- [ ] Label/tracking automation toggles default off; manual label/tracking supported.
- [ ] RMA/support is line/shipment-aware.

## NOTES

Do not let Ordering call carrier APIs directly. Fulfillment is the carrier integration boundary.

---

## Addendum — shipping grill (2026-06-18)

Expands carrier scope and rate inputs per the design grill. Carriers chosen: **DHL, Australia Post, FedEx, UPS, StarTrack, Pack & Send** (multi-origin / not single-home — origins resolved via the fulfillment-source registry / `InventoryLocation`). The quote API must be **Postman/CI-testable** against a keyless Fake; live adapters use sandbox keys (prod launch-gated).

### mt4_10 ADD remaining carrier-direct adapters (FedEx, UPS, StarTrack, Pack & Send)

- **IMPLEMENT**: Behind the same rate/label/tracking seam as mt4_4 (`ICarrierRateProvider`/`ICarrierLabelProvider`/`ICarrierTrackingProvider`), add adapters for **FedEx**, **UPS**, **StarTrack**, and **Pack & Send** alongside Australia Post + DHL and the deterministic Fake. Each is a distinct adapter (auth, payload, error mapping). Expose a clean `POST /api/shipping/quote` (origin/destination/parcel/services → rates) drivable from Postman against the Fake or sandbox.
- **PATTERN**: `IPaymentProvider`/`ISearchProvider` seam; carrier adapters mt4_4; `CarrierIntegration` lifecycle mt4_3.
- **GOTCHA**: Real carrier accounts/API access are external onboarding/launch dependencies — ship the Fake + sandbox first so UI and CI are unblocked. StarTrack/Pack & Send may share credentials/portals with AusPost — model per-source credentials, not per-brand assumptions.
- **VALIDATE**: `dotnet test src/Services/Fulfillment/tests/3commerce.Fulfillment.Tests.csproj --filter CarrierAdapter`

### mt4_11 ADD per-variant weight + dimensions with default-parcel fallback

- **IMPLEMENT**: Add weight + L/W/H to the catalog variant (carrier rate APIs require them). Importer/supplier feeds populate real values; any unmapped/seeded SKU falls back to a **configurable default parcel** so a quote always returns. Quote requests may override the parcel (for Postman/testing).
- **PATTERN**: Catalog variant model mt3_2 / mt3_10; inventory by product/variant mt4_1.
- **GOTCHA**: Weight/dims live on the catalog variant but are consumed by Fulfillment rating — project them with availability.
- **VALIDATE**: `dotnet test src/Services/Fulfillment/tests/3commerce.Fulfillment.Tests.csproj --filter Parcel`

Acceptance additions:
- [ ] DHL, Australia Post, FedEx, UPS, StarTrack, and Pack & Send adapters exist behind the seam, plus a keyless Fake.
- [ ] `POST /api/shipping/quote` is Postman/CI-testable (Fake/sandbox) and accepts an override parcel.
- [ ] Variants carry weight + dimensions with a configurable default-parcel fallback.