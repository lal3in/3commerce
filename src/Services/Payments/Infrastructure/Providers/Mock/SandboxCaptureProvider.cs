using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Infrastructure.Providers.Mock;

/// <summary>
/// Sandbox decorator (pay_3, ADR-0039): wraps the REAL provider adapter so a Sandbox host ALSO emits
/// the TEST-ONLY <c>MockPaymentCaptured</c> payload email, exactly like LocalMock, while the real test
/// credentials do the actual authorize/refund. The registry applies this decorator only when the
/// resolved <see cref="PaymentMode"/> is <see cref="PaymentMode.Sandbox"/>; Production is never wrapped
/// (no capture, no email), and the boot guard makes the mock/email config unreachable there anyway.
/// The capture is published within the caller's scope so it rides the same outbox as the real call.
/// </summary>
public sealed class SandboxCaptureProvider(IPaymentProvider inner, IMockPaymentCapture capture) : IPaymentProvider
{
    public string ProviderKey => inner.ProviderKey;

    public async Task<PaymentResponse> AuthorizeAsync(PaymentRequest request, CancellationToken ct)
    {
        var response = await inner.AuthorizeAsync(request, ct);
        await capture.CaptureAuthorizeAsync(request, response.PaymentIntentId, PaymentMode.Sandbox, ct);
        return response;
    }

    public async Task<ProviderRefundResult> RefundAsync(string paymentIntentId, long amountMinor, string idempotencyKey, CancellationToken ct)
    {
        var result = await inner.RefundAsync(paymentIntentId, amountMinor, idempotencyKey, ct);
        await capture.CaptureRefundAsync(inner.ProviderKey, paymentIntentId, amountMinor, idempotencyKey, PaymentMode.Sandbox, ct);
        return result;
    }

    public Task<string> CreateCustomerAsync(Guid userId, string email, CancellationToken ct) =>
        inner.CreateCustomerAsync(userId, email, ct);

    public Task<SetupIntentResult> CreateSetupIntentAsync(string providerCustomerId, CancellationToken ct) =>
        inner.CreateSetupIntentAsync(providerCustomerId, ct);

    public Task<SavedPaymentMethodDetails> GetPaymentMethodAsync(string providerPaymentMethodId, CancellationToken ct) =>
        inner.GetPaymentMethodAsync(providerPaymentMethodId, ct);

    public PaymentWebhookEvent? ParseWebhook(string payload, string signatureHeader, IReadOnlyList<string> secrets) =>
        inner.ParseWebhook(payload, signatureHeader, secrets);
}
