namespace ThreeCommerce.Payments.Infrastructure.Providers;

/// <summary>
/// The per-provider inbound-webhook signature header name (mt6_7 / pay_4). The generalized
/// <c>/webhooks/{provider}</c> route reads the raw header by this map and hands it to the adapter's
/// <c>ParseWebhook</c>, which owns verification. Unknown providers fall back to <c>Stripe-Signature</c>
/// so a mis-mapped provider fails closed at verification rather than reading the wrong header.
/// </summary>
public static class WebhookSignatureHeaders
{
    private static readonly IReadOnlyDictionary<string, string> Headers =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["stripe"] = "Stripe-Signature",
            ["polar"] = "Webhook-Signature",
            ["paypal"] = "Paypal-Transmission-Sig",
            ["afterpay"] = "Afterpay-Signature",
        };

    public static string For(string provider) =>
        Headers.TryGetValue(provider.Trim(), out var header) ? header : "Stripe-Signature";
}
