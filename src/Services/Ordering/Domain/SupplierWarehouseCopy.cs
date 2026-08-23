namespace ThreeCommerce.Ordering.Domain;

/// <summary>
/// Ordering's local projection of a supplier's current warehouse address, fed by the Entity service's
/// <c>SupplierWarehouseChanged</c> (ADR-0008 read model). It lets a "Collect at warehouse" checkout stamp
/// the warehouse address onto the order without querying Entity. <see cref="SupplierId"/> is the supplier's
/// Entity id — the same id <c>OfferCopy.SupplierId</c> and the order line's <c>SupplierId</c> carry, so the
/// warehouse for a cart's <c>Warehouse</c>-fulfilment line is resolved by that line's supplier.
/// </summary>
public sealed class SupplierWarehouseCopy
{
    public Guid SupplierId { get; set; }
    public Guid TenantId { get; set; }
    public required string Name { get; set; }
    public required string Line1 { get; set; }
    public string? Line2 { get; set; }
    public required string City { get; set; }
    public string? Region { get; set; }
    public required string Postcode { get; set; }
    public required string CountryCode { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
