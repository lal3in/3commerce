using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Infrastructure.Providers.PayPal;

/// <summary>
/// PayPal PSP adapter (pay_4, ADR-0039). Sandbox-ready skeleton behind the seam: resolves the
/// mode-appropriate client id + secret and the sandbox/production base URL through
/// <see cref="PaymentSecretResolver"/> (Sandbox/Production refuse to run without them) and funnels its
/// webhook into the single <c>PaymentEventProcessor</c>. The live Orders v2 call is wired only when the
/// adapter graduates (no SDK/HTTP dependency yet). PayPal amounts arrive as decimal strings; they are
/// converted to minor units for the provider-agnostic event. Webhook truth is the capture event.
/// </summary>
public sealed class PayPalPaymentProvider(IConfiguration configuration) : IPaymentProvider
{
    private const string Provider = "paypal";
    private readonly PaymentSecretResolver _secrets = new(configuration);

    public string ProviderKey => Provider;

    public Task<PaymentResponse> AuthorizeAsync(PaymentRequest request, CancellationToken ct)
    {
        var mode = ModeFor(request.Account);
        _ = _secrets.Get(Provider, mode, "ClientId"); // refuses without mode-appropriate credentials
        _ = _secrets.Get(Provider, mode, "Secret");
        _ = _secrets.BaseUrl(Provider, mode);         // sandbox vs production endpoint (production gate)

        // Skeleton: the real POST {baseUrl}/v2/checkout/orders lands here once PayPal graduates. The
        // shopper approves on PayPal, so the outcome is RequiresAction until the capture webhook lands.
        var intentId = $"paypal_{ModeTag(mode)}_{request.OrderId:N}";
        return Task.FromResult(new PaymentResponse(intentId, $"{intentId}_secret", PaymentOutcome.RequiresAction));
    }

    public Task<ProviderRefundResult> RefundAsync(string paymentIntentId, long amountMinor, string idempotencyKey, CancellationToken ct) =>
        Task.FromResult(new ProviderRefundResult($"paypal_re_{Guid.CreateVersion7():N}", Succeeded: true));

    public PaymentWebhookEvent? ParseWebhook(string payload, string signatureHeader, IReadOnlyList<string> secrets)
    {
        if (!SkeletonWebhookVerifier.Verify(payload, signatureHeader, secrets, DateTimeOffset.UtcNow))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var type = root.TryGetProperty("event_type", out var t) ? t.GetString() : null;
        var kind = type switch
        {
            "PAYMENT.CAPTURE.COMPLETED" => PaymentWebhookKind.PaymentSucceeded,
            "PAYMENT.CAPTURE.DENIED" or "PAYMENT.CAPTURE.DECLINED" => PaymentWebhookKind.PaymentFailed,
            _ => (PaymentWebhookKind?)null,
        };
        if (kind is null || !root.TryGetProperty("resource", out var resource))
        {
            return null;
        }

        var eventId = root.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty;
        var intentId = resource.TryGetProperty("id", out var pid) ? pid.GetString() ?? string.Empty : string.Empty;
        var amount = resource.TryGetProperty("amount", out var amt) && amt.TryGetProperty("value", out var v)
            ? ToMinor(v.GetString())
            : 0;
        var failure = kind == PaymentWebhookKind.PaymentFailed
            ? (resource.TryGetProperty("status_details", out var sd) && sd.TryGetProperty("reason", out var r) ? r.GetString() : null) ?? "capture denied"
            : null;

        return new PaymentWebhookEvent(eventId, kind.Value, intentId, amount, 0, failure);
    }

    // PayPal is a wallet/PSP without Stripe-style off-session saved cards in this seam; those flows use Stripe.
    public Task<string> CreateCustomerAsync(Guid userId, string email, CancellationToken ct) =>
        throw new NotSupportedException("PayPal does not support off-session customer creation; use the Stripe provider for saved methods.");

    public Task<SetupIntentResult> CreateSetupIntentAsync(string providerCustomerId, CancellationToken ct) =>
        throw new NotSupportedException("PayPal does not support setup intents.");

    public Task<SavedPaymentMethodDetails> GetPaymentMethodAsync(string providerPaymentMethodId, CancellationToken ct) =>
        throw new NotSupportedException("PayPal does not expose saved payment-method details.");

    /// <summary>Converts a PayPal decimal amount string ("49.90") to minor units (4990), rounding half-to-even.</summary>
    private static long ToMinor(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            ? (long)Math.Round(d * 100m, MidpointRounding.ToEven)
            : 0;

    private static PaymentMode ModeFor(PaymentAccountSnapshot account) =>
        account.Mode == PaymentProviderMode.Live ? PaymentMode.Production : PaymentMode.Sandbox;

    private static string ModeTag(PaymentMode mode) => mode == PaymentMode.Production ? "live" : "test";
}
