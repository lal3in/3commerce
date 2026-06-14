namespace ThreeCommerce.BuildingBlocks.Contracts.Ordering;

/// <summary>
/// Rich domain event published by the Order aggregate owner (Ordering's status updater)
/// once payment is captured. Consumed by Notifications (email) and Fulfillment (shipments).
/// </summary>
public record OrderConfirmed(
    Guid OrderId,
    string Email,
    long AmountMinor,
    string Currency,
    IReadOnlyList<OrderLineInfo> Lines);

public record OrderLineInfo(Guid ProductId, string Title, int Quantity, string FulfillmentSource);
