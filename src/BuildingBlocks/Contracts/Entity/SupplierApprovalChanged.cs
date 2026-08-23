namespace ThreeCommerce.BuildingBlocks.Contracts.Entity;

/// <summary>
/// Published by the Entity service (the supplier lifecycle owner) whenever a supplier's approval state
/// changes: <see cref="Approved"/> is true when the supplier is activated
/// (<c>SupplierOnboardingState.Active</c>), and false when it is suspended or archived. Approval-gated
/// availability (DECISION A, strict): an offer backed by an unapproved supplier never counts — not for
/// pricing, not for availability, not at checkout. Catalog and Ordering each project this into a local
/// <c>SupplierApprovalCopy</c> read model (ADR-0008) so offer resolution and storefront availability can
/// gate on "is this offer's supplier approved" without querying Entity. <see cref="SupplierId"/> is the
/// supplier's Entity id — the same id <c>Offer.SupplierId</c> carries.
/// </summary>
public sealed record SupplierApprovalChanged(
    Guid TenantId,
    Guid SupplierId,
    bool Approved);
