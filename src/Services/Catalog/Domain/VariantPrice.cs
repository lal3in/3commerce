namespace ThreeCommerce.Catalog.Domain;

/// <summary>
/// A tenant-authored explicit price for a variant in a specific currency (ADR-0015 superseded).
/// One row per currency — NOT derived by FX from a base price. A storefront shows/charges the price
/// for its own currency; a product with no row for the storefront's currency is hidden there.
/// </summary>
public class VariantPrice
{
    public Guid Id { get; init; }
    public Guid VariantId { get; init; }

    /// <summary>ISO 4217, upper-case.</summary>
    public required string Currency { get; set; }

    /// <summary>Integer minor units (AGENTS.md invariant) — never floating point.</summary>
    public long PriceMinor { get; set; }
}
