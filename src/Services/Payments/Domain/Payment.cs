using ThreeCommerce.Payments.Domain.Ledger;

namespace ThreeCommerce.Payments.Domain;

public enum PaymentStatus { Pending = 1, Succeeded = 2, Failed = 3, Refunded = 4 }

/// <summary>Tracks one order's payment intent through its lifecycle.</summary>
public class Payment
{
    public Guid Id { get; init; }
    public Guid OrderId { get; init; }

    /// <summary>
    /// The storefront this order belongs to (phase 2), carried on AuthorizePayment. Drives the
    /// per-storefront revenue/tax ledger accounts the sale posts to. Null for legacy/non-storefront
    /// payments (subscriptions, older rows) — those keep the shared revenue.sales / tax accounts.
    /// </summary>
    public Guid? StorefrontId { get; set; }
    public required string PaymentIntentId { get; set; }
    public long AmountMinor { get; init; }
    public long TaxMinor { get; init; }
    /// <summary>Shipping portion of <see cref="AmountMinor"/> — booked to income.shipping (its own P&amp;L line).</summary>
    public long ShippingMinor { get; init; }
    public required string Currency { get; init; }
    public PaymentStatus Status { get; set; }

    /// <summary>
    /// The shopper's chosen method (ADR-0039), mapped from checkout's paymentOption by
    /// <see cref="PaymentMethodKindMapper"/>. Persisted so the ledger and admin can attribute the
    /// sale to the wallet/PSP the shopper actually used. Legacy rows default to
    /// <see cref="PaymentMethodKind.Card"/>.
    /// </summary>
    public PaymentMethodKind MethodKind { get; set; } = PaymentMethodKind.Card;

    /// <summary>
    /// The lowercase provider key that settles this payment (stripe|paypal|polar|afterpay|mock).
    /// Drives the provider-scoped cash/fee ledger accounts. Legacy rows default to "stripe".
    /// </summary>
    public string Provider { get; set; } = LedgerProviders.Default;
    public string? ProviderCustomerId { get; set; }
    public string? ProviderPaymentMethodId { get; set; }
    public bool SavePaymentMethodRequested { get; set; }
    public long RefundedMinor { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}
