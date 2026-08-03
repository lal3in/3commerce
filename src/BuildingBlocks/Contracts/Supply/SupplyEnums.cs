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

public static class FulfilmentTypeExtensions
{
    /// <summary>
    /// Whether an order line moves a physical parcel — and so is charged shipping and produces a shipment.
    /// Defined as the complement of the KNOWN non-physical types (digital download / subscription / usage /
    /// manual service), so <see cref="FulfilmentType.Warehouse"/>, <see cref="FulfilmentType.Dropship"/>
    /// AND <see cref="FulfilmentType.Unassigned"/> all ship. Unassigned defaults to shippable on purpose:
    /// a physical good that merely lacks a projected supply profile must not silently lose its shipping —
    /// only an *explicitly* non-physical line withholds it. The single shared predicate for this decision.
    /// </summary>
    public static bool RequiresShipping(this FulfilmentType type) =>
        type is not (FulfilmentType.DigitalDownload or FulfilmentType.Subscription
            or FulfilmentType.Usage or FulfilmentType.ManualService);
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

/// <summary>The cadence a price is charged at (Phase 7). One-off for one-time/usage; recurring for subscriptions.</summary>
public enum BillingPeriod
{
    Once = 1,
    Monthly = 2,
    Yearly = 3,
}

/// <summary>What a usage-based product meters (Phase 7 / mt7_4).</summary>
public enum MeterType
{
    Token = 1,
    Transaction = 2,
    Request = 3,
    Minute = 4,
    Seat = 5,
    StorageGb = 6,
}
