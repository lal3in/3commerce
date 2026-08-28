namespace ThreeCommerce.Catalog.Domain;

/// <summary>
/// Tenant-scoped catalog feature switches. Keyed by <see cref="TenantId"/> (Products are tenant-scoped,
/// not storefront-scoped). Currently carries only the mandatory per-country ship-rules gate.
/// </summary>
public class TenantCatalogSettings
{
    public Guid TenantId { get; init; }

    /// <summary>
    /// When true, product create/update requires at least one <see cref="ProductShipRule"/>.
    /// </summary>
    public bool RequireProductShipRules { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
