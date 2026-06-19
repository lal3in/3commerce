namespace ThreeCommerce.Payments.Domain;

public enum PaymentStatus { Pending = 1, Succeeded = 2, Failed = 3, Refunded = 4 }

/// <summary>Tracks one order's payment intent through its lifecycle.</summary>
public class Payment
{
    public Guid Id { get; init; }
    public Guid OrderId { get; init; }
    public required string PaymentIntentId { get; set; }
    public long AmountMinor { get; init; }
    public long TaxMinor { get; init; }
    public required string Currency { get; init; }
    public PaymentStatus Status { get; set; }
    public string? ProviderCustomerId { get; set; }
    public string? ProviderPaymentMethodId { get; set; }
    public bool SavePaymentMethodRequested { get; set; }
    public long RefundedMinor { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}
