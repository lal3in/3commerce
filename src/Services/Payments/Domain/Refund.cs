namespace ThreeCommerce.Payments.Domain;

public enum RefundStatus { Pending = 1, Completed = 2, Failed = 3 }

public class Refund
{
    public Guid Id { get; init; }
    public Guid OrderId { get; init; }
    public required string PaymentIntentId { get; init; }
    public long AmountMinor { get; init; }
    public RefundStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}
