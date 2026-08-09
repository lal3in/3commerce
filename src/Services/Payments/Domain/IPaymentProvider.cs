namespace ThreeCommerce.Payments.Domain;

/// <summary>
/// The payment-rail seam (ADR-0014). v1 ships a real Stripe adapter and a deterministic
/// fake (tests / local dev without keys). Card data never reaches us — tokenization is
/// client-side (SAQ-A). The webhook is the single source of "payment happened".
/// </summary>
public interface IPaymentProvider
{
    /// <summary>
    /// Lowercase key matching <c>PaymentAccount.Provider</c> and the <c>/webhooks/{provider}</c>
    /// route. Adapters self-register in DI as <see cref="IPaymentProvider"/> and the registry
    /// resolves them by this key (the mock adapter serves the key <c>"mock"</c>).
    /// </summary>
    public string ProviderKey { get; }

    /// <summary>
    /// Authorizes a payment from a provider-agnostic <see cref="PaymentRequest"/>, returning the
    /// intent id + client secret and a typed <see cref="PaymentOutcome"/>/<see cref="PaymentError"/>.
    /// Replaces the old loose-primitive CreateIntent call; the webhook remains the trusted source
    /// of the final captured/failed outcome.
    /// </summary>
    public Task<PaymentResponse> AuthorizeAsync(PaymentRequest request, CancellationToken ct);

    public Task<string> CreateCustomerAsync(Guid userId, string email, CancellationToken ct);

    public Task<SetupIntentResult> CreateSetupIntentAsync(string providerCustomerId, CancellationToken ct);

    public Task<SavedPaymentMethodDetails> GetPaymentMethodAsync(string providerPaymentMethodId, CancellationToken ct);

    public Task<ProviderRefundResult> RefundAsync(string paymentIntentId, long amountMinor, string idempotencyKey, CancellationToken ct);

    /// <summary>
    /// Verifies signature and parses; returns null if it cannot be trusted/parsed.
    /// <paramref name="secrets"/> are the active signing secrets, newest first (def_2 registry with
    /// config fallback) — verification accepts ANY of them so rotation never drops webhooks.
    /// </summary>
    public PaymentWebhookEvent? ParseWebhook(string payload, string signatureHeader, IReadOnlyList<string> secrets);
}

public record SetupIntentResult(string SetupIntentId, string ClientSecret);

public record ProviderRefundResult(string RefundId, bool Succeeded);

/// <summary>Normalized webhook event — provider-agnostic so consumers never see Stripe types.</summary>
public record PaymentWebhookEvent(
    string EventId,
    PaymentWebhookKind Kind,
    string PaymentIntentId,
    long AmountMinor,
    long FeeMinor,
    string? FailureReason,
    string? ProviderDisputeId = null,
    string? DisputeStatusRaw = null);

/// <summary>
/// The normalized payment/dispute event set. Wire values are stable and numeric (platform invariant).
/// <see cref="ChargebackOpened"/> is retained as the legacy alias for "funds withdrawn on a dispute" and is
/// treated identically to <see cref="DisputeFundsWithdrawn"/> by the processor.
/// </summary>
public enum PaymentWebhookKind
{
    PaymentSucceeded = 1,          // payment_intent.succeeded — settlement
    PaymentFailed = 2,             // payment_intent.payment_failed — rejection (insufficient funds, mandate, …)
    ChargebackOpened = 3,          // legacy alias of DisputeFundsWithdrawn
    PaymentVoided = 4,             // payment_intent.canceled — the authorization was voided
    DisputeCreated = 5,            // charge.dispute.created
    DisputeUpdated = 6,            // charge.dispute.updated
    DisputeFundsWithdrawn = 7,     // charge.dispute.funds_withdrawn
    DisputeFundsReinstated = 8,    // charge.dispute.funds_reinstated
    DisputeClosedWon = 9,          // charge.dispute.closed, status = won
    DisputeClosedLost = 10,        // charge.dispute.closed, status = lost → chargeback + void record
}
