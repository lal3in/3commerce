namespace ThreeCommerce.Fulfillment.Domain;

public enum HoldReason { Fraud = 1, Payment = 2, Address = 3, Inventory = 4, Manual = 5 }

public enum HoldStatus { Active = 1, Released = 2 }

/// <summary>
/// A hold that blocks an order from being fulfilled (mt4_9): fraud/payment/address/inventory or a
/// manual operator hold. Fulfillment defers an order while any hold is Active; releasing the last
/// one lets the captured order fulfil.
/// </summary>
public sealed class OrderHold
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid OrderId { get; init; }
    public HoldReason Reason { get; init; }
    public HoldStatus Status { get; private set; } = HoldStatus.Active;
    public string? Note { get; private set; }
    public string? PlacedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ReleasedAt { get; private set; }

    public bool IsActive => Status == HoldStatus.Active;

    private OrderHold() { }

    public static OrderHold Place(Guid tenantId, Guid orderId, HoldReason reason, string? note, string? placedBy, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty || orderId == Guid.Empty)
        {
            throw new FulfillmentRuleException("Hold tenant and order are required.");
        }

        return new OrderHold
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OrderId = orderId,
            Reason = reason,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            PlacedBy = placedBy,
            CreatedAt = now,
        };
    }

    public void Release(DateTimeOffset now)
    {
        Status = HoldStatus.Released;
        ReleasedAt = now;
    }
}

/// <summary>
/// A confirmed order captured because it was held (mt4_9). Stores the OrderConfirmed payload so the
/// order can be fulfilled once all holds clear, without re-publishing.
/// </summary>
public sealed class HeldOrder
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid OrderId { get; init; }
    public required string PayloadJson { get; init; }
    public bool Fulfilled { get; private set; }
    public DateTimeOffset CreatedAt { get; init; }

    public void MarkFulfilled() => Fulfilled = true;
}
