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
    string Currency = "",
    // The product's nature (ADR-0028), projected onto OfferCopy so checkout can apply the tenant's
    // ProductType shipping policy to each line. Appended, back-compatible default; a copy that still
    // carries the default 0/Physical from before this field means "unknown" at checkout (falls back to
    // the fulfilment-type gate).
    ProductType ProductType = ProductType.Physical,
    // Offer-as-price (ADR-0028): the offer's authoritative selling price + its storefront scope and active
    // window, projected onto OfferCopy so an active, in-window offer for the line's storefront sets the
    // charged price instead of the catalog SellingPriceMinor. Appended, back-compatible defaults — a copy
    // from before these fields carries PriceMinor 0 (no offer price → checkout keeps the catalog price),
    // StorefrontId null (all storefronts), and an open-ended window.
    long PriceMinor = 0,
    Guid? StorefrontId = null,
    DateTimeOffset? ActiveFrom = null,
    DateTimeOffset? ActiveUntil = null);
