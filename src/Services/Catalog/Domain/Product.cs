namespace ThreeCommerce.Catalog.Domain;

/// <summary>
/// Neutral internal product schema (ADR-0004): supplier-specific data stays in
/// SupplierRef/Attributes so any future feed maps onto this without migration.
/// </summary>
public class Product
{
    public Guid Id { get; init; }
    public required string Slug { get; set; }
    public required string Title { get; set; }
    public required string Brand { get; set; }
    public string Description { get; set; } = string.Empty;
    public Guid CategoryId { get; set; }
    public Dictionary<string, string> Attributes { get; set; } = [];
    public List<string> ImageUrls { get; set; } = [];
    public string? SupplierRef { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<Variant> Variants { get; init; } = [];
}

public class Variant
{
    public Guid Id { get; init; }
    public Guid ProductId { get; init; }
    public required string Sku { get; set; }
    /// <summary>Integer minor units (AGENTS.md invariant) — never floating point.</summary>
    public long PriceMinor { get; set; }
    /// <summary>ISO 4217. Single configured store currency in v1 (ADR-0015).</summary>
    public required string Currency { get; set; }
    public int StockQuantity { get; set; }
}

public class Category
{
    public Guid Id { get; init; }
    public required string Slug { get; set; }
    public required string Name { get; set; }
    public Guid? ParentId { get; set; }
}
