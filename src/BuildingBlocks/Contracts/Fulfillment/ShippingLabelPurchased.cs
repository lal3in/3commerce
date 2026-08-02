namespace ThreeCommerce.BuildingBlocks.Contracts.Fulfillment;

/// <summary>
/// Published when a carrier label is bought with a non-zero cost (phase 1). Consumed by Payments to
/// book the carrier-cost accrual (Dr shipping-cost expense / Cr liability.carrier_payable).
/// </summary>
public record ShippingLabelPurchased(Guid PackageId, Guid ShipmentId, Guid OrderId, Guid TenantId, string Carrier, long CostMinor, string Currency);
