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
    bool IsLive,
    // ADR-0038: inclusive regimes (AuGst/EuVat) — the tenant's shelf price already CONTAINS the tax
    // (checkout extracts it informationally); exclusive regimes add it. Optional → back-compatible.
    bool TaxInclusive = false,
    // Ship-to allowlist (ISO 3166-1 alpha-2). Empty = ships worldwide; non-empty = checkout rejects a
    // ship-to country not in the list. A concrete array (not IReadOnlyList) so MassTransit's serializer
    // materializes it on the consumer; optional → back-compatible with older publishers/consumers.
    string[]? ShipToCountries = null);
