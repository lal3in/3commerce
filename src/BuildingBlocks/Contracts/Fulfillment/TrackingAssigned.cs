namespace ThreeCommerce.BuildingBlocks.Contracts.Fulfillment;

/// <summary>Consumed by Notifications (email) and Ordering (order tracking projection).</summary>
public record TrackingAssigned(Guid ShipmentId, Guid OrderId, string Carrier, string TrackingNumber);
