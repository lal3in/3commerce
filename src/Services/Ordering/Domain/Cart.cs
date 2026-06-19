namespace ThreeCommerce.Ordering.Domain;

/// <summary>
/// Anonymous (cookie-keyed) or user-owned cart. Item prices are snapshotted from the
/// ProductCopy at add-time and re-validated at checkout (plan task 4/5).
/// </summary>
public class Cart
{
    public Guid Id { get; init; }
    /// <summary>Opaque cookie key for anonymous carts; null once owned by a user.</summary>
    public string? CartKey { get; set; }
    public Guid? UserId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<CartItem> Items { get; init; } = [];
}

public class CartItem
{
    public Guid Id { get; init; }
    public Guid CartId { get; init; }
    public Guid ProductId { get; init; }
    public Guid? VariantId { get; init; }
    public string? VariantSku { get; set; }
    public required string Slug { get; set; }
    public required string Title { get; set; }
    public string? ImageUrl { get; set; }
    public long UnitPriceMinor { get; set; }
    public required string Currency { get; set; }
    public int Quantity { get; set; }
}
