using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Workers.Notifications.Domain;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill;

/// <summary>Which consumer a backfilled <c>OrderConfirmed</c> is sent to (ADR-0060). Never both by Publish.</summary>
public enum BackfillTarget
{
    Fulfillment = 1,
    Notifications = 2,
}

/// <summary>
/// The facts about one confirmed order the selection needs — no PII beyond the email, which is used only to match
/// the delivery log and is never printed.
/// </summary>
public sealed record OrderFacts(
    Guid OrderId,
    Guid TenantId,
    long PublicOrderNumber,
    OrderStatus Status,
    bool Disputed,
    string Email,
    DateTimeOffset ConfirmedAt,
    bool HasShippableLine)
{
    /// <summary>
    /// <see cref="Order.CreatedAt"/> is the confirmation instant: the order row is created FROM the checkout
    /// attempt by <c>OrderStatusConsumer</c> when payment is captured (<c>CheckoutAttempt.ToOrder(…, now)</c>).
    /// <see cref="HasShippableLine"/> uses the same predicate Fulfillment ships by
    /// (<see cref="FulfilmentTypeExtensions.RequiresShipping"/>: Warehouse, Dropship AND Unassigned).
    /// </summary>
    public static OrderFacts From(Order order) => new(
        order.Id,
        order.TenantId,
        order.PublicOrderNumber,
        order.Status,
        order.Disputed,
        order.Email,
        order.CreatedAt,
        order.Lines.Any(l => l.FulfilmentType.RequiresShipping()));
}

/// <summary>One row of the Notifications delivery log that is (or may be) an order-confirmation email.</summary>
public sealed record DeliveryFact(string Recipient, NotificationStatus Status, DateTimeOffset OccurredAt, string? Reference);

/// <summary>What the delivery log proves about an order's confirmation email.</summary>
public enum EmailEvidence
{
    /// <summary>A Sent delivery carries this order's reference (<c>order-confirmed:{id}</c>) — exact.</summary>
    EmailedByReference = 1,

    /// <summary>A legacy (reference-less) Sent delivery to the order's email can only belong to this order.</summary>
    EmailedByTimeMatch = 2,

    /// <summary>No Sent delivery to the order's email could belong to it — it never got the email.</summary>
    Missing = 3,

    /// <summary>Deliveries to this email exist that might be this order's, but the log cannot say which order each was for.</summary>
    Ambiguous = 4,

    /// <summary>The order was confirmed before the delivery log has any row — there is no evidence either way.</summary>
    PredatesDeliveryLog = 5,
}

/// <summary>Why an order is not eligible for any backfill send, whatever the evidence says.</summary>
public enum SkipReason
{
    None = 0,

    /// <summary>Fully refunded: shipping it or confirming it now would be wrong.</summary>
    Refunded = 1,

    /// <summary>Marked delivered by an operator: it reached the customer by some other path.</summary>
    Delivered = 2,

    /// <summary>Under a chargeback: an operator decides, not a backfill.</summary>
    Disputed = 3,
}
