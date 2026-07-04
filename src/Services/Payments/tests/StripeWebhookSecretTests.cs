using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using ThreeCommerce.Payments.Infrastructure.Stripe;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// Rotation-safe webhook verification (def_2): ParseWebhook accepts a payload signed with ANY of
/// the supplied secrets (registry newest-first + fallback) and rejects everything else. Signatures
/// are computed exactly as Stripe does: HMAC-SHA256 over "{timestamp}.{payload}".
/// </summary>
public class StripeWebhookSecretTests
{
    // Stripe.net's Event parser requires api_version + request to be present; the provider passes
    // throwOnApiVersionMismatch:false so api_version doesn't have to match the SDK's pin.
    private const string Payload =
        """{"id":"evt_test_1","object":"event","api_version":"2025-01-01","request":{"id":"req_1","idempotency_key":null},"type":"payment_intent.succeeded","data":{"object":{"id":"pi_test_1","object":"payment_intent","amount_received":4200}}}""";

    private static StripePaymentProvider Provider() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Stripe:SecretKey"] = "sk_test_dummy" })
        .Build());

    private static string Sign(string secret, string payload)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{payload}")));
        return $"t={timestamp},v1={signature}";
    }

    [Fact]
    public void Accepts_a_payload_signed_with_the_newest_secret()
    {
        var ev = Provider().ParseWebhook(Payload, Sign("whsec_new", Payload), ["whsec_new", "whsec_old"]);

        Assert.NotNull(ev);
        Assert.Equal("pi_test_1", ev.PaymentIntentId);
        Assert.Equal(4200, ev.AmountMinor);
    }

    [Fact]
    public void Accepts_a_payload_still_signed_with_the_old_secret_during_rotation()
    {
        var ev = Provider().ParseWebhook(Payload, Sign("whsec_old", Payload), ["whsec_new", "whsec_old"]);

        Assert.NotNull(ev);
    }

    [Fact]
    public void Rejects_a_payload_signed_with_a_deactivated_or_unknown_secret()
    {
        Assert.Null(Provider().ParseWebhook(Payload, Sign("whsec_revoked", Payload), ["whsec_new", "whsec_old"]));
        Assert.Null(Provider().ParseWebhook(Payload, Sign("whsec_new", Payload), []));
    }
}
