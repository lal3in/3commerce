using ThreeCommerce.BuildingBlocks.Contracts.Supply;

namespace ThreeCommerce.Ordering.Domain;

/// <summary>
/// Local read copy of a Catalog Offer (ADR-0028 / ADR-0008), kept current via OfferChanged events.
/// Checkout resolves each line's fulfilment type from these without a cross-service query.
/// </summary>
public class OfferCopy
{
    public Guid OfferId { get; init; }
    public Guid TenantId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? VariantId { get; set; }
    public Guid SupplierId { get; set; }
    public FulfilmentType FulfilmentType { get; set; }
    public int Priority { get; set; }
    public bool Active { get; set; }
}

/// <summary>Picks the offer that fulfils a line (ADR-0028): a variant-specific offer beats a
/// product-level one; ties break on lowest priority; no active offer means none.</summary>
public static class OfferResolution
{
    public static OfferCopy? ResolveOffer(
        IEnumerable<OfferCopy> offers, Guid tenantId, Guid productId, Guid? variantId) =>
        offers
            .Where(o => o.Active && o.TenantId == tenantId && o.ProductId == productId
                && (o.VariantId == variantId || o.VariantId == null))
            .OrderByDescending(o => o.VariantId == variantId)
            .ThenBy(o => o.Priority)
            .FirstOrDefault();

    public static FulfilmentType ResolveFulfilment(
        IEnumerable<OfferCopy> offers, Guid tenantId, Guid productId, Guid? variantId) =>
        ResolveOffer(offers, tenantId, productId, variantId)?.FulfilmentType ?? FulfilmentType.Unassigned;
}
