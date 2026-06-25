using ThreeCommerce.BuildingBlocks.Contracts.Supply;

namespace ThreeCommerce.BuildingBlocks.Contracts.Fulfillment;

public record ShipmentCreated(Guid ShipmentId, Guid OrderId, FulfilmentType FulfilmentType);
