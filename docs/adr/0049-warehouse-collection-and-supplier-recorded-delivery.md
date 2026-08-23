# 0049 — Warehouse collection at checkout, and supplier-recorded delivery

Status: Accepted
Area: Entity / Ordering / Fulfillment / supplier portal
Extends: [0028](./0028-product-supply-profiles-composable-supply.md) (Offers / fulfilment types), [0042](./0042-physical-shipping-carriers-and-product-type-policy.md) (shipping charged only for shippable carts), [0025](./0025-pdp-pep-dynamic-rbac.md) (maker-checker change requests), [0027](./0027-entity-service-master-data-boundary.md) (Entity owns supplier master data), [0008](./0008-database-per-service-single-postgres.md) (read-copy projections)

## Context

A supplier that backs `Warehouse`-fulfilment offers (ADR-0028) has a physical warehouse, but the platform
had no home for that warehouse's **address**, no way for a shopper to **collect** goods from it instead of
paying a carrier, and no way for the fulfilling supplier to record that an order was **delivered/collected**.
Concretely, before this decision:

- Supplier addresses existed as generic Entity addresses, but no warehouse address flowed to Ordering, so
  checkout had nothing to collect *from*.
- Every physical cart was charged a carrier rate (ADR-0042); there was no zero-shipping pickup path.
- `OrderStatus` ended at `Confirmed`; nothing moved an order to a delivered/collected terminal state, and
  suppliers had no portal surface to report fulfilment.

This is the final slice of the supplier/offer epic (builds on the approval lock of ADR-0025/self-service
and approval-gated availability, ADR-0048).

## Decision

1. **The supplier's warehouse address is Entity master data, projected to Ordering.** A supplier manages a
   `Warehouse`-purpose address from the Supplier Portal **Warehouse** page, under the **same approval lock**
   as its other details (ADR-0025): edited **directly** while not yet approved, and via a maker-checker
   **`SupplierChangeRequest.WarehouseAddress`** (type 5) once Active. Entity publishes
   **`SupplierWarehouseChanged`** (supplier id + name + address) on set/edit/approved-change; Ordering
   projects it into a local **`SupplierWarehouseCopy`** read model (ADR-0008) so checkout never queries
   Entity.

2. **"Collect at warehouse" is a checkout delivery option with zero shipping and no carrier.** The
   checkout request carries `CollectAtWarehouse`. It is eligible only when the cart has at least one
   **physical `Warehouse`-fulfilment line from an approved supplier** (ADR-0048); an ineligible collect
   request is rejected so the client falls back to a shipped rate. A collect order sets **`ShippingMinor = 0`**,
   selects **no carrier**, skips carrier-rate validation, and records the warehouse it is collected from
   (`Order.CollectAtWarehouse` + `Warehouse*` snapshot fields, from `SupplierWarehouseCopy`). This is the
   pickup counterpart to ADR-0042's rule that only shippable carts pay shipping — normal shipped/dropship
   flows are untouched.

3. **A fulfilling supplier (or an operator) records delivery.** `OrderStatus` gains a terminal
   **`Delivered`** (6). `POST /orders/supplier/{id}/mark-delivered` transitions a `Confirmed` order to
   `Delivered` and publishes **`OrderDelivered`**. It is **idempotent** (an already-`Delivered` order returns
   200, no second event) and **authorized to the fulfilling supplier only** — a supplier whose
   `supplier_entity` matches a line's `SupplierId` — or an operator (admin/master); any other signed-in
   supplier is forbidden. Supplier logins **self-scope** to their own `supplier_entity` claim (never a
   client-supplied id). Suppliers see their orders on a Supplier Portal **Orders** page (orders carrying a
   line they fulfil).

4. **Fulfillment closes shipments on the event.** An `OrderDelivered` consumer moves every non-delivered
   shipment for the order to `ShipmentStatus.Delivered` — including a collect order's warehouse shipment
   (picked up rather than dispatched). Idempotent; a digital-only order with no shipments is a harmless
   no-op. Notifications sends the delivery confirmation.

## Alternatives considered

- **Model the warehouse as a new first-class aggregate/service now.** Deferred: a `Warehouse`-purpose
  Entity address + a projected read copy meets the checkout and portal needs without a new DB-owning
  service, consistent with the epic's "wire existing Entity machinery" posture. A dedicated warehouse/
  location model can extract later if multi-warehouse-per-supplier or capacity/hours are needed.
- **A generic zero-cost shipping rate instead of a distinct collect flag.** Rejected: collection is a
  different delivery *mode* (no carrier, an address to collect *from*, not ship *to*), and shopper/portal
  UX and the order record need to say "collect at warehouse", not "free shipping".
- **Let any supplier or only admins mark delivered.** Rejected both extremes — delivery authority belongs
  to the supplier that actually fulfils the line (self-scoped), with operators as a backstop.

## Consequences

- Suppliers self-serve their warehouse address (locked post-approval like every other detail) and report
  delivery from the portal; shoppers of warehouse-stocked physical goods can collect for free.
- `Order` gains `CollectAtWarehouse` + a warehouse-address snapshot and the `Delivered` status; migrations
  land in Ordering, with `SupplierWarehouseCopy` as a new read model. Fulfillment gains an `OrderDelivered`
  consumer.
- Two new cross-service contracts — `SupplierWarehouseChanged` (Entity → Ordering) and `OrderDelivered`
  (Ordering → Fulfillment/Notifications) — keep the flow event-driven with no synchronous cross-service
  calls (ADR-0008).
- Collect orders correctly bypass the carrier path and post zero shipping to the per-storefront ledger
  (ADR-0045), so no phantom shipping income/cost is booked for a pickup.
