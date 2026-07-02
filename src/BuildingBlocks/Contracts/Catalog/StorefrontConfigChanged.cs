namespace ThreeCommerce.BuildingBlocks.Contracts.Catalog;

/// <summary>
/// Published whenever a storefront's shopper-facing commerce config changes (create/update/lifecycle).
/// Other services keep a local read copy (ADR-0008) — e.g. Ordering resolves the tax rate to charge by
/// the cart's currency. TaxRateBasisPoints is the tenant-set rate (1000 = 10%); IsLive = Active/Preview.
/// </summary>
public record StorefrontConfigChanged(
    Guid StorefrontId,
    Guid TenantId,
    string Name,
    string Currency,
    int TaxRateBasisPoints,
    bool IsLive);
