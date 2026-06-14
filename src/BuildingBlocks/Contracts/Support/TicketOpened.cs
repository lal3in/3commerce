namespace ThreeCommerce.BuildingBlocks.Contracts.Support;

public record TicketOpened(Guid TicketId, Guid OrderId, string Email, string Reason);
