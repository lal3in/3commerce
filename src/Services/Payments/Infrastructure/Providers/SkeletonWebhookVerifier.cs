using ThreeCommerce.BuildingBlocks.Infrastructure.Webhooks;

namespace ThreeCommerce.Payments.Infrastructure.Providers;

/// <summary>
/// Sandbox-skeleton webhook signature verification shared by the pay_4 HMAC providers (Polar, PayPal,
/// Afterpay). Each provider's real signature scheme differs (PayPal is asymmetric cert-chain, Polar is
/// Standard Webhooks, Afterpay is HMAC); until a provider graduates past its skeleton they all verify a
/// constant-time HMAC over <c>"{timestamp}.{payload}"</c> via <see cref="InboundWebhookVerifier"/> with a
/// Stripe-style <c>t=&lt;unix&gt;,v1=&lt;hex&gt;</c> header, rotation-safe over the def_2 secret set. The
/// header format and scheme are the ONLY skeleton part — the parsed event shape below is the real
/// provider-agnostic contract that flows into <c>PaymentEventProcessor</c>.
/// </summary>
internal static class SkeletonWebhookVerifier
{
    /// <summary>
    /// Verifies <paramref name="signatureHeader"/> (<c>t=&lt;unix&gt;,v1=&lt;hex&gt;</c>) against ANY of the
    /// active <paramref name="secrets"/> (newest first, def_2). Returns false on a malformed header, a
    /// stale timestamp (±5-min tolerance), or no matching secret — the caller then drops the event.
    /// </summary>
    public static bool Verify(string payload, string signatureHeader, IReadOnlyList<string> secrets, DateTimeOffset now)
    {
        if (secrets.Count == 0 || !TryParse(signatureHeader, out var timestamp, out var signatureHex))
        {
            return false;
        }

        foreach (var secret in secrets)
        {
            if (InboundWebhookVerifier.Verify(secret, payload, timestamp, signatureHex, now))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParse(string header, out long timestamp, out string signatureHex)
    {
        timestamp = 0;
        signatureHex = string.Empty;
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        foreach (var part in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2)
            {
                continue;
            }

            switch (kv[0])
            {
                case "t":
                    _ = long.TryParse(kv[1], out timestamp);
                    break;
                case "v1":
                    signatureHex = kv[1];
                    break;
            }
        }

        return timestamp > 0 && signatureHex.Length > 0;
    }
}
