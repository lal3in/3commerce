namespace ThreeCommerce.BuildingBlocks.Contracts.Ordering;

/// <summary>
/// Internal: the checkout saga signals payment settled. The Order aggregate owner then
/// marks the order Confirmed and publishes the rich OrderConfirmed with line items.
/// </summary>
public record CheckoutCompleted(Guid OrderId);
