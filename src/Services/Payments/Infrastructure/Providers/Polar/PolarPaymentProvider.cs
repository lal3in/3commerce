using System.Text.Json;
using Microsoft.Extensions.Configuration;
using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Infrastructure.Providers.Polar;

/// <summary>
/// Polar merchant-of-record adapter (pay_4, ADR-0039). Sandbox-ready skeleton behind the seam: it
/// resolves mode-appropriate credentials + the sandbox/production base URL through
/// <see cref="PaymentSecretResolver"/> (so a Sandbox/Production host refuses to run without them) and
/// funnels its webhook into the single <c>PaymentEventProcessor</c>. The live HTTP call to Polar's
/// checkout API is wired only when this adapter graduates past skeleton (no SDK/HTTP dependency yet).
/// Money truth is the webhook, exactly like Stripe.
/// </summary>
public sealed class PolarPaymentProvider(IConfiguration configuration) : IPaymentProvider
{
    private const string Provider = "polar";
    private readonly PaymentSecretResolver _secrets = new(configuration);

    public string ProviderKey => Provider;

    public Task<PaymentResponse> AuthorizeAsync(PaymentRequest request, CancellationToken ct)
    {
        var mode = ModeFor(request.Account);
        _ = _secrets.Get(Provider, mode, "AccessToken"); // refuses without a mode-appropriate credential
        _ = _secrets.BaseUrl(Provider, mode);            // sandbox vs production endpoint (production gate)

        // Skeleton: the real POST {baseUrl}/v1/checkouts lands here once Polar graduates. The shopper is
        // redirected to Polar's hosted checkout, so the outcome is RequiresAction until the webhook lands.
        var intentId = $"polar_{ModeTag(mode)}_{request.OrderId:N}";
        return Task.FromResult(new PaymentResponse(intentId, $"{intentId}_secret", PaymentOutcome.RequiresAction));
    }

    public Task<ProviderRefundResult> RefundAsync(string paymentIntentId, long amountMinor, string idempotencyKey, CancellationToken ct) =>
        Task.FromResult(new ProviderRefundResult($"polar_re_{Guid.CreateVersion7():N}", Succeeded: true));

    public PaymentWebhookEvent? ParseWebhook(string payload, string signatureHeader, IReadOnlyList<string> secrets)
    {
        if (!SkeletonWebhookVerifier.Verify(payload, signatureHeader, secrets, DateTimeOffset.UtcNow))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        var kind = type switch
        {
            "order.paid" => PaymentWebhookKind.PaymentSucceeded,
            "order.payment_failed" => PaymentWebhookKind.PaymentFailed,
            _ => (PaymentWebhookKind?)null,
        };
        if (kind is null || !root.TryGetProperty("data", out var data))
        {
            return null;
        }

        var eventId = root.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty;
        var intentId = data.TryGetProperty("id", out var pid) ? pid.GetString() ?? string.Empty : string.Empty;
        var amount = data.TryGetProperty("amount", out var amt) && amt.TryGetInt64(out var minor) ? minor : 0;
        var failure = kind == PaymentWebhookKind.PaymentFailed
            ? (data.TryGetProperty("failure_reason", out var fr) ? fr.GetString() : null) ?? "payment failed"
            : null;

        return new PaymentWebhookEvent(eventId, kind.Value, intentId, amount, 0, failure);
    }

    // Polar is a merchant-of-record: it does not expose Stripe-style off-session customers / setup
    // intents / saved payment methods. Those flows resolve the default (Stripe) provider, never this one.
    public Task<string> CreateCustomerAsync(Guid userId, string email, CancellationToken ct) =>
        throw new NotSupportedException("Polar does not support off-session customer creation; use the Stripe provider for saved methods.");

    public Task<SetupIntentResult> CreateSetupIntentAsync(string providerCustomerId, CancellationToken ct) =>
        throw new NotSupportedException("Polar does not support setup intents.");

    public Task<SavedPaymentMethodDetails> GetPaymentMethodAsync(string providerPaymentMethodId, CancellationToken ct) =>
        throw new NotSupportedException("Polar does not expose saved payment-method details.");

    private static PaymentMode ModeFor(PaymentAccountSnapshot account) =>
        account.Mode == PaymentProviderMode.Live ? PaymentMode.Production : PaymentMode.Sandbox;

    private static string ModeTag(PaymentMode mode) => mode == PaymentMode.Production ? "live" : "test";
}
