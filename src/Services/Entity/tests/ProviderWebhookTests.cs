using ThreeCommerce.BuildingBlocks.Infrastructure.Webhooks;

namespace ThreeCommerce.Entity.Tests;

/// <summary>mt6_7: the owning-service verification convention for inbound provider webhooks.</summary>
public class ProviderWebhookTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 25, 12, 0, 0, TimeSpan.Zero);
    private const string Secret = "whsec_abc123";
    private const string Payload = "{\"id\":\"evt_1\",\"type\":\"payment.succeeded\"}";

    [Fact]
    public void Accepts_a_fresh_valid_signature()
    {
        var ts = Now.ToUnixTimeSeconds();
        var signature = WebhookSignature.Compute(Secret, ts, Payload);
        Assert.True(InboundWebhookVerifier.Verify(Secret, Payload, ts, signature, Now));
    }

    [Fact]
    public void Rejects_a_wrong_signature()
    {
        var ts = Now.ToUnixTimeSeconds();
        Assert.False(InboundWebhookVerifier.Verify(Secret, Payload, ts, "deadbeef", Now));
    }

    [Fact]
    public void Rejects_a_signature_made_with_a_different_secret()
    {
        var ts = Now.ToUnixTimeSeconds();
        var foreignSignature = WebhookSignature.Compute("other-secret", ts, Payload);
        Assert.False(InboundWebhookVerifier.Verify(Secret, Payload, ts, foreignSignature, Now));
    }

    [Fact]
    public void Rejects_a_replayed_stale_timestamp()
    {
        var staleTs = Now.AddMinutes(-10).ToUnixTimeSeconds();
        var signature = WebhookSignature.Compute(Secret, staleTs, Payload); // correctly signed, but old
        Assert.False(InboundWebhookVerifier.Verify(Secret, Payload, staleTs, signature, Now));
    }

    [Fact]
    public void Rejects_a_far_future_timestamp()
    {
        var futureTs = Now.AddMinutes(10).ToUnixTimeSeconds();
        var signature = WebhookSignature.Compute(Secret, futureTs, Payload);
        Assert.False(InboundWebhookVerifier.Verify(Secret, Payload, futureTs, signature, Now));
    }

    [Fact]
    public void Honours_a_custom_tolerance()
    {
        var ts = Now.AddMinutes(-10).ToUnixTimeSeconds();
        var signature = WebhookSignature.Compute(Secret, ts, Payload);
        Assert.True(InboundWebhookVerifier.Verify(Secret, Payload, ts, signature, Now, TimeSpan.FromMinutes(15)));
    }
}
