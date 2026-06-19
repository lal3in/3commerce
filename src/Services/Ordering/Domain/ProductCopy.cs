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
}

public class ProductVariantCopy
{
    public Guid VariantId { get; init; }
    public Guid ProductId { get; init; }
    public required string Sku { get; set; }
    public long PriceMinor { get; set; }
    public required string Currency { get; set; }
    public int StockQuantity { get; set; }
}
