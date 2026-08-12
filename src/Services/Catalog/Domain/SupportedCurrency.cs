namespace ThreeCommerce.Catalog.Domain;

/// <summary>
/// Catalog's local projection of the tenant currency registry (currency_2), fed by the Entity service's
/// <c>CurrencyChanged</c> (ADR-0008 read model, mirroring <c>StorefrontServiceReadiness</c>). Storefront
/// commerce config and product/offer pricing validate their currency against this — only a registered,
/// <see cref="Enabled"/> code may be newly used. Disabled codes stay projected so history keeps resolving.
/// </summary>
public sealed class SupportedCurrency
{
    public Guid TenantId { get; set; }
    public required string Code { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public int DecimalPlaces { get; set; } = 2;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; }
}
