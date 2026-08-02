using ThreeCommerce.BuildingBlocks.Contracts.Supply;

namespace ThreeCommerce.BuildingBlocks.Contracts.Catalog;

/// <summary>
/// Published when an Offer (product supply profile, ADR-0028) is configured or changed. Feeds
/// Ordering's local OfferCopy so checkout can resolve each line's fulfilment type/source without a
/// cross-service query (ADR-0008). Carries Active so a deactivated offer is excluded from selection.
/// SupplierCostMinor + Currency (appended, back-compatible defaults) carry the per-unit COGS so a paid
/// order can accrue it per supplier — the cost is denominated in Currency, never converted (no FX).
/// </summary>
public record OfferChanged(
    Guid OfferId,
    Guid TenantId,
    Guid ProductId,
    Guid? VariantId,
    Guid SupplierId,
    SupplyCategory SupplyCategory,
    FulfilmentType FulfilmentType,
    PricingModel PricingModel,
    BillingPeriod BillingPeriod,
    int Priority,
    bool Active,
    long SupplierCostMinor = 0,
    string Currency = "");
