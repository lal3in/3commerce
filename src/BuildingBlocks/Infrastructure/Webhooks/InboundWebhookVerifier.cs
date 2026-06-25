namespace ThreeCommerce.BuildingBlocks.Infrastructure.Webhooks;

/// <summary>
/// Receiver-side verification for an inbound provider webhook (mt6_7), for providers that sign with a
/// shared-secret HMAC over "{timestamp}.{payload}" (the symmetric counterpart of mt6_6's outbound
/// signing; Stripe-style). The owning service — never the gateway — calls this: it checks the HMAC in
/// constant time AND that the timestamp is within tolerance, so a captured request can't be replayed.
/// Idempotency (dedupe by the provider's event id) is the service's inbox, e.g. Payments' WebhookInbox.
/// </summary>
public static class InboundWebhookVerifier
{
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    public static bool Verify(
        string secret, string payload, long timestampUnix, string signatureHex, DateTimeOffset now, TimeSpan? tolerance = null)
    {
        var window = tolerance ?? DefaultTolerance;
        var age = now - DateTimeOffset.FromUnixTimeSeconds(timestampUnix);

        // Reject stale (replayed) or too-far-future timestamps before the (constant-time) signature check.
        if (age > window || age < -window)
        {
            return false;
        }

        return WebhookSignature.Verify(secret, timestampUnix, payload, signatureHex);
    }
}
