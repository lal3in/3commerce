namespace ThreeCommerce.BuildingBlocks.Contracts.Fulfillment;

public record ShipmentCreated(Guid ShipmentId, Guid OrderId, string FulfillmentSource);
