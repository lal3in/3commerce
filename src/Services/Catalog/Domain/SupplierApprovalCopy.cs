namespace ThreeCommerce.Catalog.Domain;

/// <summary>
/// Catalog's local projection of a supplier's approval state (DECISION A, strict), fed by the Entity
/// service's <c>SupplierApprovalChanged</c> (ADR-0008 read model, mirroring <see cref="SupportedCurrency"/>).
/// It is the source of truth for "is this offer's supplier approved" when Catalog resolves storefront
/// availability and offer-as-price: an offer whose <c>SupplierId</c> has no row here, or a row with
/// <see cref="Approved"/> false, never counts. <see cref="SupplierId"/> is the supplier's Entity id — the
/// same id <c>Offer.SupplierId</c> carries.
/// </summary>
public sealed class SupplierApprovalCopy
{
    public Guid SupplierId { get; set; }
    public Guid TenantId { get; set; }
    public bool Approved { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
