namespace ThreeCommerce.Ordering.Domain;

/// <summary>
/// Local read copy of a storefront's tax config, fed by Catalog's StorefrontConfigChanged events
/// (ADR-0008: no cross-service queries). Checkout resolves the rate to charge by the cart's currency.
/// </summary>
public class StorefrontTaxCopy
{
    public Guid StorefrontId { get; init; }
    public Guid TenantId { get; set; }
    public required string Currency { get; set; }

    /// <summary>Tenant-set tax rate in basis points (1000 = 10%).</summary>
    public int TaxRateBasisPoints { get; set; }

    /// <summary>Active/Preview storefronts only participate in checkout tax resolution.</summary>
    public bool IsLive { get; set; }
}
