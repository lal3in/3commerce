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

/// <summary>
/// What a <see cref="Payment"/> pays for (renewal_usage_refund_path, ADR-0041/0057 amendments 2026-10-28). An order's
/// checkout payment is the only one keyed by its order; a renewal or usage charge is its own payment, keyed by its
/// <see cref="Payment.ChargeReference"/> (the reference of the journal entry that booked it), so the refund path,
/// partial refunds and the dispute webhooks reach it without ever touching the order's own payment.
/// </summary>
public enum PaymentKind
{
    /// <summary>An order's checkout payment — one per order (the legacy meaning of every row).</summary>
    Order = 1,

    /// <summary>A settled subscription renewal (<c>renew-{subscriptionId}-{sequence}</c>).</summary>
    Renewal = 2,

    /// <summary>A settled metered usage charge — overage or prepaid auto-load (<c>overage-…</c> / <c>autoload-…</c>).</summary>
    UsageCharge = 3,
}

/// <summary>Tracks one payment intent through its lifecycle: an order's checkout payment, or a renewal / usage charge.</summary>
public class Payment
{
    public Guid Id { get; init; }

    /// <summary>
    /// The order this payment belongs to. A renewal carries its subscription's order (several rows per order); a usage
    /// charge has none (<see cref="Guid.Empty"/>). Only an <see cref="PaymentKind.Order"/> payment is unique per order.
    /// </summary>
    public Guid OrderId { get; init; }

    /// <summary>What this payment pays for; legacy rows are order payments.</summary>
    public PaymentKind Kind { get; init; } = PaymentKind.Order;

    /// <summary>
    /// For a renewal / usage charge: the reference of the journal entry that booked it (<c>renew-{id}-{seq}</c>,
    /// <c>overage-…</c>, <c>autoload-…</c>) — unique, the key a refund names the charge by, and where a reversal takes its
    /// tax from. Null for an order payment (its sale entry is referenced by the order id).
    /// </summary>
    public string? ChargeReference { get; init; }

    /// <summary>The subscription a renewal payment renewed (null otherwise).</summary>
    public Guid? SubscriptionId { get; init; }

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

    /// <summary>
    /// The reference of this payment's dispute reversal entry: <c>{orderId}:chargeback</c> for an order payment (one per
    /// order, unchanged), <c>{chargeReference}:chargeback</c> for a renewal / usage charge — so disputing a renewal never
    /// collides with (or is mistaken for) the order's own chargeback.
    /// </summary>
    public string ChargebackReference => Kind == PaymentKind.Order ? $"{OrderId}:chargeback" : $"{ChargeReference}:chargeback";

    /// <summary>
    /// A settled renewal / usage charge, recorded in the SAME unit of work as the journal entry that booked it
    /// (<paramref name="entry"/>). Its tax is read from that entry — the tax account's credit
    /// (<see cref="Ledger.Ledger.TaxOf"/>) — so a refund or dispute reverses exactly the tax this charge posted, never one
    /// recomputed at today's rate or from the subscription's price.
    /// </summary>
    public static Payment ForSettledCharge(
        PaymentKind kind, JournalEntry entry, string? taxAccount, Guid orderId, Guid? subscriptionId, string paymentIntentId,
        long grossMinor, string currency, string provider, Guid? storefrontId, string? providerCustomerId,
        string? providerPaymentMethodId, DateTimeOffset now)
    {
        if (kind == PaymentKind.Order)
        {
            throw new ArgumentException("An order payment is created by checkout (AuthorizePayment), not as a settled charge.", nameof(kind));
        }

        return new Payment
        {
            Id = Guid.CreateVersion7(),
            Kind = kind,
            ChargeReference = entry.Reference,
            OrderId = orderId,
            SubscriptionId = subscriptionId,
            StorefrontId = storefrontId,
            PaymentIntentId = paymentIntentId,
            AmountMinor = grossMinor,
            TaxMinor = Ledger.Ledger.TaxOf(entry, taxAccount),
            Currency = currency,
            Status = PaymentStatus.Succeeded,
            MethodKind = PaymentMethodKind.Card,
            Provider = string.IsNullOrWhiteSpace(provider) ? LedgerProviders.Default : provider,
            ProviderCustomerId = providerCustomerId,
            ProviderPaymentMethodId = providerPaymentMethodId,
            CreatedAt = StoredTime.Of(now),
        };
    }
}
