using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Infrastructure.Payments;

/// <summary>
/// Deterministic provider for tests and local dev without Stripe keys (ADR-0015: build
/// never blocks on the business). Intent creation is synchronous; "payment happened" is
/// simulated through the dev-only simulate endpoint feeding the SAME webhook processor.
/// </summary>
public sealed class FakePaymentProvider : IPaymentProvider
{
    public Task<PaymentIntentResult> CreateIntentAsync(Guid orderId, long amountMinor, string currency, string idempotencyKey, CancellationToken ct)
    {
        var intentId = $"pi_fake_{orderId:N}";
        return Task.FromResult(new PaymentIntentResult(intentId, $"{intentId}_secret_test"));
    }

    public Task<ProviderRefundResult> RefundAsync(string paymentIntentId, long amountMinor, string idempotencyKey, CancellationToken ct) =>
        Task.FromResult(new ProviderRefundResult($"re_fake_{Guid.CreateVersion7():N}", Succeeded: true));

    // Real webhooks come from Stripe; the fake path uses the dev simulate endpoint instead.
    public PaymentWebhookEvent? ParseWebhook(string payload, string signatureHeader) => null;

    /// <summary>Deterministic fake fee (2.9% + 30 minor units) so the ledger fee line is exercised.</summary>
    public static long FakeFee(long grossMinor) => (long)Math.Round(grossMinor * 0.029) + 30;
}
