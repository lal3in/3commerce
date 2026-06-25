namespace ThreeCommerce.BuildingBlocks.Contracts.Fulfillment;

/// <summary>
/// Fulfillment (the single stock owner, ADR-0028) announces the new sellable availability for a
/// (product, variant). Catalog consumes it to keep its variant stock read model in sync — Catalog
/// never owns stock, it only mirrors. Idempotent: it carries the absolute available quantity.
/// </summary>
public record InventoryAvailabilityChanged(Guid TenantId, Guid ProductId, Guid? VariantId, int Available);
