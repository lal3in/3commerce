# 0048 — Supplier-approval-gated offer validity and availability (Decision A, strict)

Status: Accepted — implemented (Entity publish + Catalog/Ordering projections + availability/checkout/pricing gate)
Area: Entity / Catalog / Ordering / availability
Extends: [0027](./0027-entity-service-master-data-boundary.md) (Entity owns the supplier lifecycle), [0028](./0028-product-supply-profiles-composable-supply.md) (Offers), [0047](./0047-storefront-scoped-active-window-offer-price.md) (offer-as-price), [0008](./0008-database-per-service-single-postgres.md) (read-copy projections)

## Context

A supplier moves through an onboarding lifecycle owned by the Entity service (ADR-0027):
`Draft → PendingVerification → PendingApproval → Active → Suspended / Archived`
(`SupplierOnboarding`). A tenant admin **approves** a supplier by activating it. The
supplier↔product/variant link is the **Offer** (ADR-0028), which carries `SupplierId`.

Before this decision, an Offer counted for pricing (ADR-0047), fulfilment, availability, and checkout
**regardless of whether its supplier had been approved.** That means an operator could stand up a product
and start selling it against a supplier that had not passed verification/approval — the platform would
show a price, show availability, and take an order it could not responsibly source.

The question this ADR settles: **when may an offer count?** The scope discussion weighed a nuanced answer
(e.g. "owned warehouse stock still sells even if the supplier is unapproved; only dropship goes
out-of-stock") against a strict one.

## Decision

**Decision A (strict): an offer backed by an unapproved supplier never counts — for anything.**

1. **Approved = `SupplierOnboardingState.Active`.** Only an Active supplier is approved. A supplier that
   is `Draft`, `PendingVerification`, or `PendingApproval` is **not yet** approved; a `Suspended` or
   `Archived` supplier is **no longer** approved.

2. **An unapproved supplier's offer is invalid everywhere — no price, no availability, no checkout.** It
   contributes no storefront price (overriding ADR-0047 for that offer), no listing/detail availability,
   and cannot be selected at checkout. This holds **regardless of fulfilment type or on-hand stock** —
   even owned-warehouse stock behind an unapproved supplier's offer does not sell. (The nuanced
   "warehouse still sells / dropship OOS" carve-out was **explicitly rejected**.)

3. **A variant is unavailable unless another *approved* offer covers it.** Availability is the OR over the
   variant's offers whose supplier is approved. If every covering offer is unapproved, the variant is
   unavailable (hidden / not purchasable), exactly as if it had no offer at all.

4. **Entity is the source of truth; approval status reaches Catalog and Ordering by projection
   (ADR-0008), never by cross-service query.** The Entity service publishes
   **`SupplierApprovalChanged(TenantId, SupplierId, Approved)`** whenever a supplier's approval state
   changes — `Approved = true` on activate, `false` on suspend and on archive-of-an-active supplier. It is
   published **inside the same transaction** as the state change (before `SaveChanges`, the repo outbox
   convention) so the outbox row commits and is actually delivered. `SupplierId` is the supplier's Entity
   id — the same id `Offer.SupplierId` carries. Catalog and Ordering each project it into a local
   `SupplierApprovalCopy` read model (idempotent upsert, mirroring the currency projection), and offer
   resolution / storefront availability gate on "is this offer's supplier approved" from that copy.

5. **Offerless products keep their catalog behaviour (scope guard).** The gate is an OR over a variant's
   *covering* offers; a product/variant with **no** covering offer has no supplier to gate and is
   unaffected. So the strict rule narrows availability only where an offer exists and every covering
   offer's supplier is unapproved.

## Alternatives considered

- **Nuanced gating (owned warehouse stock still sells; only dropship goes OOS).** Rejected: it makes
  "can I sell this?" depend on a matrix of fulfilment type × stock × approval, is hard to reason about and
  to test, and still books an order against a supplier the tenant has not approved. Strict is simpler and
  safer, and can be relaxed later if a real tenant needs it (revisit trigger).
- **Query Entity for approval status at checkout / listing time.** Rejected — violates the read-copy
  boundary (ADR-0008) and puts a synchronous cross-service call on the hot storefront/checkout path.
- **Gate only checkout, not display.** Rejected — it reintroduces shown≠sellable: the storefront would
  advertise a price/availability that checkout then refuses.

## Consequences

- Approving a supplier (Admin **Suppliers** page → Approve, or the Entity `suppliers/activate` endpoint)
  is what makes its offers go live; suspending/archiving it withdraws them everywhere. Approval is
  therefore a real go-live lever, consistent with the storefront go-live gate (ADR-0043).
- Availability becomes a function of **both** stock/window/effective-offer (ADR-0047) **and** supplier
  approval. A product can be fully priced and in stock yet correctly unavailable because its only offer's
  supplier is not approved.
- Requires a `SupplierApprovalCopy` read model in **both** Catalog (storefront listing / detail) and
  Ordering (checkout offer resolution), each fed by a `SupplierApprovalChanged` consumer.
- **Implementation (complete).** The full path is wired:
  - **Entity** publishes `SupplierApprovalChanged` on activate (`true`) / suspend / archive-of-active
    (`false`), in the state-change transaction (`SupplierOnboardingService`).
  - **Catalog** projects it into `SupplierApprovalCopy` (`SupplierApprovalConsumer`) and gates the public
    listing and detail (`ProductsEndpoints`): a product/variant whose covering offers are **all** from
    unapproved suppliers is dropped from the listing / marked unavailable (out-of-stock) on detail, and
    only an **approved** offer can set the shown offer price.
  - **Ordering** projects it into its own `SupplierApprovalCopy` (`SupplierApprovalChangedConsumer`) and
    threads an `approvedSupplierIds` set through every `OfferResolution` call in `CheckoutEndpoints`, so
    unapproved offers count for neither pricing, fulfilment, shipping gate, recurring-line detection, nor
    the order line's supplier ref. A line whose only covering offer is unapproved is rejected with
    *"… is currently unavailable and was removed; its supplier is not approved."*
- This is a strict superset constraint over [0047](./0047-storefront-scoped-active-window-offer-price.md):
  offer-as-price still decides *which* price, but an unapproved supplier's offer is filtered out first.
