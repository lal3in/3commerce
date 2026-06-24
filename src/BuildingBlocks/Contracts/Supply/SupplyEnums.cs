namespace ThreeCommerce.BuildingBlocks.Contracts.Supply;

/// <summary>
/// How an order line is sourced and delivered (ADR-0028). One shared vocabulary across
/// Catalog, Ordering, and Fulfillment — replaces the duplicated per-service enums and the
/// stringly-typed bus field. Int values are stable for persistence: 0/1/2 preserve the legacy
/// Unassigned / Dropship / Warehouse (was OwnWarehouse) values so no data migration is needed.
/// </summary>
public enum FulfilmentType
{
    Unassigned = 0,
    Dropship = 1,
    Warehouse = 2,
    DigitalDownload = 3,
    Subscription = 4,
    Usage = 5,
    ManualService = 6,
}

/// <summary>The nature of how a product is supplied (ADR-0028) — distinct from the product type.</summary>
public enum SupplyCategory
{
    Physical = 1,
    Digital = 2,
    Service = 3,
}

/// <summary>How an order line is charged. One-time today; recurring/metered land in Phase 7.</summary>
public enum BillingMode
{
    OneTime = 1,
    Recurring = 2,
    Metered = 3,
}

/// <summary>How an Offer's price is structured (ADR-0028). Only OneTime is exercised before Phase 7.</summary>
public enum PricingModel
{
    OneTime = 1,
    Subscription = 2,
    UsageBased = 3,
    Tiered = 4,
}
