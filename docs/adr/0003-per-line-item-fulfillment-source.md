# ADR-0003: Fulfillment source tracked per order line item

- **Status:** Accepted
- **Date:** 2026-06-12
- **Source:** PRD design interview (decision log #3, `docs/prd/3commerce/15-appendix.md`)

## Context

Whether goods are dropshipped by suppliers or warehoused and shipped by us is **undecided** — no supplier is signed and the business model may mix both. Order schemas that assume a single fulfillment model are painful to retrofit.

## Decision

Every order line item carries a `FulfillmentSource` field (`Unassigned` | `Dropship(supplierId)` | `OwnWarehouse`) from day one. `Unassigned` is valid in v1.

## Alternatives considered

- **Commit to dropshipping now** — rejected: premature, no supplier exists.
- **Order-level fulfillment source** — rejected: a single order may mix sources.

## Consequences

- Fulfillment service groups shipments by per-line source.
- The dropship-vs-warehouse decision is deferred without schema risk; choosing later is config + importer work, not a migration.

## Later changes (recorded 2026-11-27)

- **Generalized by [ADR-0028](./0028-product-supply-profiles-composable-supply.md).** The per-line principle stands,
  but the `FulfillmentSource {Unassigned, Dropship(supplierId), OwnWarehouse}` field became a typed
  `FulfilmentType` (`Unassigned`, `Dropship`, `Warehouse`, `DigitalDownload`, `Subscription`, `Usage`,
  `ManualService`, …) plus a separate resolved `SupplierId` and `BillingMode` on each order line
  (`src/Services/Ordering/Domain/Order.cs`), from one shared vocabulary in `BuildingBlocks.Contracts.Supply`.
  The line's supply is resolved from its Offer at checkout.
