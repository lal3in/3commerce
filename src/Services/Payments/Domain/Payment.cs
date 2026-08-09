using ThreeCommerce.Payments.Domain.Ledger;

namespace ThreeCommerce.Payments.Domain;

public enum PaymentStatus { Pending = 1, Succeeded = 2, Failed = 3, Refunded = 4, Disputed = 5, Chargeback = 6, Voided = 7 }

/// <summary>
/// The dispute lifecycle sub-status surfaced on a payment's gateway-transaction-status field. Distinct
/// from <see cref="PaymentStatus"/> (the money state): a payment can be <see cref="PaymentStatus.Disputed"/>
/// while its dispute is still <see cref="UnderReview"/>, and only a <see cref="Lost"/> dispute moves the
/// payment to <see cref="PaymentStatus.Chargeback"/>. Mirrors the provider dispute states
/// (Stripe/Polar: needs_response/under_review/won/lost + the funds withdrawn/reinstated transitions).
/// </summary>
public enum DisputeStatus
{
    None = 0,
    Created = 1,          // charge.dispute.created — funds typically held/withdrawn at creation
    UnderReview = 2,      // charge.dispute.updated → provider status under_review
    FundsWithdrawn = 3,   // charge.dispute.funds_withdrawn — the reversal is booked
    FundsReinstated = 4,  // charge.dispute.funds_reinstated — funds returned to the merchant
    Won = 5,              // charge.dispute.closed, status = won
    Lost = 6,             // charge.dispute.closed, status = lost → PaymentStatus.Chargeback + void record
}

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

    /// <summary>
    /// The dispute lifecycle sub-status (the gateway-transaction-status field). <see cref="DisputeStatus.None"/>
    /// until a <c>charge.dispute.*</c> event lands. Set alongside <see cref="Status"/> by the webhook
    /// processor so operators see both the money state and where the dispute sits.
    /// </summary>
    public DisputeStatus DisputeStatus { get; set; } = DisputeStatus.None;

    /// <summary>The provider's dispute id (Stripe <c>dp_…</c> / Polar equivalent), once a dispute opens.</summary>
    public string? ProviderDisputeId { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}
