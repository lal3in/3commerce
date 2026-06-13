namespace ThreeCommerce.Payments.Domain;

/// <summary>
/// The payment-rail seam (ADR-0014). v1 ships a real Stripe adapter and a deterministic
/// fake (tests / local dev without keys). Card data never reaches us — tokenization is
/// client-side (SAQ-A). The webhook is the single source of "payment happened".
/// </summary>
public interface IPaymentProvider
{
    public Task<PaymentIntentResult> CreateIntentAsync(Guid orderId, long amountMinor, string currency, string idempotencyKey, CancellationToken ct);

    public Task<ProviderRefundResult> RefundAsync(string paymentIntentId, long amountMinor, string idempotencyKey, CancellationToken ct);

    /// <summary>Verifies signature and parses; returns null if it cannot be trusted/parsed.</summary>
    public PaymentWebhookEvent? ParseWebhook(string payload, string signatureHeader);
}

public record PaymentIntentResult(string PaymentIntentId, string ClientSecret);

public record ProviderRefundResult(string RefundId, bool Succeeded);

/// <summary>Normalized webhook event — provider-agnostic so consumers never see Stripe types.</summary>
public record PaymentWebhookEvent(
    string EventId,
    PaymentWebhookKind Kind,
    string PaymentIntentId,
    long AmountMinor,
    long FeeMinor,
    string? FailureReason);

public enum PaymentWebhookKind { PaymentSucceeded = 1, PaymentFailed = 2 }
