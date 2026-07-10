using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Infrastructure.Providers.Mock;

/// <summary>
/// The LocalMock payment adapter (pay_3, ADR-0039). No external calls: it deterministically simulates
/// all six <see cref="MockScenario"/> outcomes (success / failure / declined card / expired card /
/// 3DS required / cancelled) and, on every authorize and refund, publishes the TEST-ONLY
/// <c>MockPaymentCaptured</c> event (the redacted "what we would have sent" payload) that the
/// Notifications worker renders as a "TEST ONLY / MOCK PAYMENT" email.
/// <para>
/// It composes <see cref="FakePaymentProvider"/> for the deterministic core — customers, setup-intents,
/// saved-method details, refund shape, and the <c>pi_fake_…</c> intent id — so the Success path and the
/// <c>/dev/simulate-payment</c> → <c>PaymentEventProcessor</c> → ledger funnel are byte-for-byte the
/// same as before. Serves the <c>"mock"</c> provider key; <see cref="ParseWebhook"/> returns null (the
/// mock path uses the dev simulate endpoint, not real webhooks).
/// </para>
/// </summary>
public sealed class MockEmailPaymentProvider(IMockPaymentCapture capture) : IPaymentProvider
{
    private readonly FakePaymentProvider _core = new();

    public string ProviderKey => "mock";

    public async Task<PaymentResponse> AuthorizeAsync(PaymentRequest request, CancellationToken ct)
    {
        // Deterministic intent id from the fake core (pi_fake_…) so the simulate/ledger funnel is unchanged.
        var baseResponse = await _core.AuthorizeAsync(request, ct);
        var intentId = baseResponse.PaymentIntentId;

        // Capture the FULL would-be payload (redacted) BEFORE any state save — it rides the consumer's
        // outbox so it commits atomically with the pending Payment (publish-before-SaveChangesAsync).
        await capture.CaptureAuthorizeAsync(request, intentId, PaymentMode.LocalMock, ct);

        return (request.Scenario ?? MockScenario.Success) switch
        {
            MockScenario.Success => baseResponse,
            MockScenario.Requires3ds => new PaymentResponse(
                intentId, baseResponse.ClientSecret, PaymentOutcome.RequiresAction,
                new PaymentError(PaymentErrorCode.AuthenticationRequired, "3DS authentication required (mock).", Retryable: true)),
            MockScenario.Cancelled => new PaymentResponse(
                intentId, null, PaymentOutcome.Cancelled),
            MockScenario.DeclinedCard => new PaymentResponse(
                intentId, null, PaymentOutcome.Failed,
                new PaymentError(PaymentErrorCode.CardDeclined, "Card declined (mock).", Retryable: false)),
            MockScenario.ExpiredCard => new PaymentResponse(
                intentId, null, PaymentOutcome.Failed,
                new PaymentError(PaymentErrorCode.ExpiredCard, "Card expired (mock).", Retryable: false)),
            _ => new PaymentResponse(
                intentId, null, PaymentOutcome.Failed,
                new PaymentError(PaymentErrorCode.ProcessingError, "Payment failed (mock).", Retryable: true)),
        };
    }

    public Task<string> CreateCustomerAsync(Guid userId, string email, CancellationToken ct) =>
        _core.CreateCustomerAsync(userId, email, ct);

    public Task<SetupIntentResult> CreateSetupIntentAsync(string providerCustomerId, CancellationToken ct) =>
        _core.CreateSetupIntentAsync(providerCustomerId, ct);

    public Task<SavedPaymentMethodDetails> GetPaymentMethodAsync(string providerPaymentMethodId, CancellationToken ct) =>
        _core.GetPaymentMethodAsync(providerPaymentMethodId, ct);

    public async Task<ProviderRefundResult> RefundAsync(string paymentIntentId, long amountMinor, string idempotencyKey, CancellationToken ct)
    {
        await capture.CaptureRefundAsync(ProviderKey, paymentIntentId, amountMinor, idempotencyKey, PaymentMode.LocalMock, ct);
        return await _core.RefundAsync(paymentIntentId, amountMinor, idempotencyKey, ct);
    }

    public PaymentWebhookEvent? ParseWebhook(string payload, string signatureHeader, IReadOnlyList<string> secrets) => null;
}
