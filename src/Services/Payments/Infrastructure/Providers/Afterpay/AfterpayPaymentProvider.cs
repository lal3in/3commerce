using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Infrastructure.Providers.Afterpay;

/// <summary>
/// Afterpay BNPL adapter (pay_4, ADR-0039). Sandbox-ready skeleton behind the seam: resolves the
/// mode-appropriate merchant id + secret and the sandbox/production base URL through
/// <see cref="PaymentSecretResolver"/> (Sandbox/Production refuse to run without them) and funnels its
/// webhook into the single <c>PaymentEventProcessor</c>. The live checkout/capture call is wired only
/// when the adapter graduates (no SDK/HTTP dependency yet). Afterpay amounts arrive as decimal strings.
/// A tenant needing Afterpay only as a Stripe payment method uses <c>PaymentMethodKind.Afterpay</c> on
/// the Stripe account instead of this adapter (ADR-0039 graduation choice).
/// </summary>
public sealed class AfterpayPaymentProvider(IConfiguration configuration) : IPaymentProvider
{
    private const string Provider = "afterpay";
    private readonly PaymentSecretResolver _secrets = new(configuration);

    public string ProviderKey => Provider;

    public Task<PaymentResponse> AuthorizeAsync(PaymentRequest request, CancellationToken ct)
    {
        var mode = ModeFor(request.Account);
        _ = _secrets.Get(Provider, mode, "MerchantId"); // refuses without mode-appropriate credentials
        _ = _secrets.Get(Provider, mode, "SecretKey");
        _ = _secrets.BaseUrl(Provider, mode);           // sandbox vs production endpoint (production gate)

        // Skeleton: the real POST {baseUrl}/v2/checkouts lands here once Afterpay graduates. The shopper
        // completes the BNPL flow on Afterpay, so the outcome is RequiresAction until the webhook lands.
        var intentId = $"afterpay_{ModeTag(mode)}_{request.OrderId:N}";
        return Task.FromResult(new PaymentResponse(intentId, $"{intentId}_secret", PaymentOutcome.RequiresAction));
    }

    public Task<ProviderRefundResult> RefundAsync(string paymentIntentId, long amountMinor, string idempotencyKey, CancellationToken ct) =>
        Task.FromResult(new ProviderRefundResult($"afterpay_re_{Guid.CreateVersion7():N}", Succeeded: true));

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
            "payment.approved" => PaymentWebhookKind.PaymentSucceeded,
            "payment.declined" => PaymentWebhookKind.PaymentFailed,
            _ => (PaymentWebhookKind?)null,
        };
        if (kind is null || !root.TryGetProperty("data", out var data))
        {
            return null;
        }

        var eventId = root.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty;
        var intentId = data.TryGetProperty("id", out var pid) ? pid.GetString() ?? string.Empty : string.Empty;
        var amount = data.TryGetProperty("amount", out var amt) && amt.TryGetProperty("amount", out var v)
            ? ToMinor(v.GetString())
            : 0;
        var failure = kind == PaymentWebhookKind.PaymentFailed
            ? (data.TryGetProperty("reason", out var r) ? r.GetString() : null) ?? "payment declined"
            : null;

        return new PaymentWebhookEvent(eventId, kind.Value, intentId, amount, 0, failure);
    }

    // Afterpay is BNPL, not a Stripe-style off-session card vault; saved-method flows use the Stripe provider.
    public Task<string> CreateCustomerAsync(Guid userId, string email, CancellationToken ct) =>
        throw new NotSupportedException("Afterpay does not support off-session customer creation; use the Stripe provider for saved methods.");

    public Task<SetupIntentResult> CreateSetupIntentAsync(string providerCustomerId, CancellationToken ct) =>
        throw new NotSupportedException("Afterpay does not support setup intents.");

    public Task<SavedPaymentMethodDetails> GetPaymentMethodAsync(string providerPaymentMethodId, CancellationToken ct) =>
        throw new NotSupportedException("Afterpay does not expose saved payment-method details.");

    /// <summary>Converts an Afterpay decimal amount string ("49.90") to minor units (4990), rounding half-to-even.</summary>
    private static long ToMinor(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            ? (long)Math.Round(d * 100m, MidpointRounding.ToEven)
            : 0;

    private static PaymentMode ModeFor(PaymentAccountSnapshot account) =>
        account.Mode == PaymentProviderMode.Live ? PaymentMode.Production : PaymentMode.Sandbox;

    private static string ModeTag(PaymentMode mode) => mode == PaymentMode.Production ? "live" : "test";
}
