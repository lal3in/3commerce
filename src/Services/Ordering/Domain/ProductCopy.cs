namespace ThreeCommerce.Ordering.Domain;

/// <summary>
/// Local read copy of a Catalog product, kept current via ProductUpserted events
/// (ADR-0008: no cross-service queries). The cart and checkout price against this.
/// </summary>
public class ProductCopy
{
    public Guid ProductId { get; init; }
    public required string Slug { get; set; }
    public required string Title { get; set; }
    public long MinPriceMinor { get; set; }
    public long SupplierCostMinor { get; set; }
    public long SellingPriceMinor { get; set; }
    public TaxMode TaxMode { get; set; } = TaxMode.Exclusive;
    public Guid? StorefrontId { get; set; }
    public required string Currency { get; set; }
    public string? ImageUrl { get; set; }
    public List<ProductVariantCopy> Variants { get; init; } = [];

    /// <summary>
    /// Per-destination ship rules projected from Catalog (ADR-0008). EMPTY = no per-country overrides
    /// (worldwide, taxed, shipping charged as normal). Checkout resolves the rule via <see cref="RuleFor"/>.
    /// </summary>
    public List<ProductShipRule> ShipRules { get; set; } = [];

    /// <summary>
    /// Resolves the ship rule for <paramref name="country"/> (caller pre-uppercases): a specific-country
    /// rule wins over the <c>*</c> whole-world default; null when neither is present.
    /// </summary>
    public ProductShipRule? RuleFor(string country) =>
        ShipRules.FirstOrDefault(r => r.CountryCode == country)
        ?? ShipRules.FirstOrDefault(r => r.CountryCode == "*");
}

/// <summary>
/// Ordering-side copy of a Catalog <c>ProductShipRule</c>. Record so EF's ValueComparer compares
/// structurally. <see cref="ChargeDestinationTax"/> = false excludes the line from the tax base;
/// <see cref="ShippingCovered"/> = true waives shipping when every line is covered.
/// </summary>
public sealed record ProductShipRule(string CountryCode, bool ChargeDestinationTax, bool ShippingCovered);

public class ProductVariantCopy
{
    public Guid VariantId { get; init; }
    public Guid ProductId { get; init; }
    public required string Sku { get; set; }
    public long PriceMinor { get; set; }
    public required string Currency { get; set; }
    public int StockQuantity { get; set; }

    /// <summary>Tenant-authored explicit prices per currency (projected from ProductUpserted). Empty = only the base price.</summary>
    public List<ProductVariantCopyPrice> Prices { get; init; } = [];

    /// <summary>The price to charge in <paramref name="currency"/>, or null if the tenant set none (product hidden there).</summary>
    public long? PriceInCurrency(string currency)
    {
        var cur = currency.Trim().ToUpperInvariant();
        var match = Prices.FirstOrDefault(p => p.Currency == cur);
        if (match is not null)
        {
            return match.PriceMinor;
        }

        // Fall back to the base price only when it is already in the requested currency.
        return string.Equals(Currency, cur, StringComparison.OrdinalIgnoreCase) ? PriceMinor : null;
    }
}

public class ProductVariantCopyPrice
{
    public Guid Id { get; init; }
    public Guid VariantId { get; init; }
    public required string Currency { get; set; }
    public long PriceMinor { get; set; }
}
