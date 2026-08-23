namespace ThreeCommerce.Ordering.Domain;

/// <summary>
/// Ordering's local projection of a supplier's approval state (DECISION A, strict), fed by the Entity
/// service's <c>SupplierApprovalChanged</c> (ADR-0008 read model). It is the source of truth for "is this
/// offer's supplier approved" when checkout resolves each line's pricing/fulfilment/COGS: an offer whose
/// <c>SupplierId</c> has no row here, or a row with <see cref="Approved"/> false, never counts — a line
/// whose only covering offers are unapproved has no valid supply and is rejected at checkout.
/// <see cref="SupplierId"/> is the supplier's Entity id — the same id <c>OfferCopy.SupplierId</c> carries.
/// </summary>
public sealed class SupplierApprovalCopy
{
    public Guid SupplierId { get; set; }
    public Guid TenantId { get; set; }
    public bool Approved { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
