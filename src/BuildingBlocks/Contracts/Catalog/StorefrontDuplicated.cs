namespace ThreeCommerce.BuildingBlocks.Contracts.Catalog;

/// <summary>
/// Published when a storefront is duplicated (catalog.storefront.duplicate). Services that keep
/// per-storefront config cloned alongside the new store react to this — e.g. Fulfillment copies the
/// source storefront's carrier accounts (config + credential reference) to the new storefront so the
/// duplicate ships with the same carriers. Idempotent by (tenant, new storefront).
/// </summary>
public record StorefrontDuplicated(
    Guid TenantId,
    Guid SourceStorefrontId,
    Guid NewStorefrontId,
    string Name);
