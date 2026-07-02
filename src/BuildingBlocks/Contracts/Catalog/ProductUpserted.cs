namespace ThreeCommerce.BuildingBlocks.Contracts.Catalog;

/// <summary>
/// Published on every product create/update (price changes included — no separate
/// ProductPriceChanged in v1). Feeds other services' local read copies (ADR-0008).
/// </summary>
public record ProductUpserted(
    Guid ProductId,
    string Slug,
    string Title,
    long MinPriceMinor,
    string Currency,
    string? ImageUrl,
    IReadOnlyList<ProductVariantUpserted> Variants);

public record ProductVariantUpserted(
    Guid VariantId,
    string Sku,
    long PriceMinor,
    string Currency,
    int StockQuantity,
    // Tenant-authored explicit prices per currency (no FX). Optional/back-compatible: null or empty
    // means only the base PriceMinor/Currency applies. Consumers price by the storefront currency.
    IReadOnlyList<VariantCurrencyPrice>? Prices = null);

public record VariantCurrencyPrice(string Currency, long PriceMinor);
