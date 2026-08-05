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

    /// <summary>The product's nature (ADR-0028), projected from Catalog so checkout can apply the tenant's
    /// ProductType shipping policy per line. 0 = unknown (a copy from before this field, or a missing
    /// product) — checkout then falls back to the fulfilment-type shipping gate for that line.</summary>
    public ProductType ProductType { get; set; }

    public PricingModel PricingModel { get; set; } = PricingModel.OneTime;
    public BillingPeriod BillingPeriod { get; set; } = BillingPeriod.Once;
    public int Priority { get; set; }
    public bool Active { get; set; }

    /// <summary>Per-unit supplier cost (COGS, phase 1), minor units in <see cref="Currency"/>. 0 = unknown.</summary>
    public long SupplierCostMinor { get; set; }

    /// <summary>The currency <see cref="SupplierCostMinor"/> is denominated in — a paid order only accrues
    /// COGS from this offer when it matches the order's currency (no FX in this codebase).</summary>
    public string Currency { get; set; } = "";

    /// <summary>How this offer's line is charged (Phase 7): recurring for subscriptions, metered for usage.</summary>
    public BillingMode BillingMode => PricingModel switch
    {
        PricingModel.Subscription => BillingMode.Recurring,
        PricingModel.UsageBased => BillingMode.Metered,
        _ => BillingMode.OneTime,
    };
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
