using System.Net;
using ThreeCommerce.BuildingBlocks.Infrastructure.Webhooks;

namespace ThreeCommerce.Entity.Tests;

public class WebhookDeliveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 25, 0, 0, 0, TimeSpan.Zero);

    // ---- signing ----

    [Fact]
    public void Signature_is_deterministic_and_verifies()
    {
        var sig = WebhookSignature.Compute("secret", 1_700_000_000, "{\"a\":1}");
        Assert.Equal(sig, WebhookSignature.Compute("secret", 1_700_000_000, "{\"a\":1}"));
        Assert.True(WebhookSignature.Verify("secret", 1_700_000_000, "{\"a\":1}", sig));
    }

    [Fact]
    public void Signature_rejects_a_tampered_payload_or_secret()
    {
        var sig = WebhookSignature.Compute("secret", 1_700_000_000, "{\"a\":1}");
        Assert.False(WebhookSignature.Verify("secret", 1_700_000_000, "{\"a\":2}", sig));
        Assert.False(WebhookSignature.Verify("other", 1_700_000_000, "{\"a\":1}", sig));
    }

    // ---- endpoint validation (anti-SSRF) ----

    [Theory]
    [InlineData("https://hooks.example.com/3c")]
    [InlineData("https://203.0.113.10/hook")]
    public void Public_https_endpoints_are_allowed(string url)
    {
        Assert.True(WebhookEndpoint.IsAllowed(url, out _));
    }

    [Theory]
    [InlineData("http://hooks.example.com/3c")]      // not https
    [InlineData("https://localhost/hook")]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://10.0.0.5/hook")]
    [InlineData("https://192.168.1.1/hook")]
    [InlineData("https://169.254.1.1/hook")]
    [InlineData("https://gateway.internal/hook")]
    [InlineData("not-a-url")]
    public void Unsafe_endpoints_are_rejected(string url)
    {
        Assert.False(WebhookEndpoint.IsAllowed(url, out var error));
        Assert.NotNull(error);
    }

    // ---- delivery state machine ----

    private static WebhookDelivery Queued() =>
        WebhookDelivery.Queue(Guid.NewGuid(), Guid.NewGuid(), "evt-1", "order.confirmed", Now);

    [Fact]
    public void A_queued_delivery_is_due_now()
    {
        var delivery = Queued();
        Assert.Equal(WebhookDeliveryStatus.Pending, delivery.Status);
        Assert.True(delivery.IsDue(Now));
    }

    [Fact]
    public void Success_marks_delivered()
    {
        var delivery = Queued();
        delivery.RecordSuccess(200, Now);
        Assert.Equal(WebhookDeliveryStatus.Delivered, delivery.Status);
        Assert.Equal(1, delivery.Attempts);
        Assert.Null(delivery.NextAttemptAt);
        Assert.NotNull(delivery.DeliveredAt);
    }

    [Fact]
    public void Failure_schedules_a_backed_off_retry_then_exhausts()
    {
        var delivery = Queued();
        delivery.RecordFailure(500, "HTTP 500", Now);
        Assert.Equal(WebhookDeliveryStatus.Retrying, delivery.Status);
        Assert.Equal(Now + WebhookDelivery.Backoff(1), delivery.NextAttemptAt);

        for (var i = delivery.Attempts; i < WebhookDelivery.MaxAttempts; i++)
        {
            delivery.RecordFailure(500, "HTTP 500", Now);
        }

        Assert.Equal(WebhookDeliveryStatus.Exhausted, delivery.Status);
        Assert.Null(delivery.NextAttemptAt);
        Assert.Equal(WebhookDelivery.MaxAttempts, delivery.Attempts);
    }

    [Fact]
    public void Backoff_grows_and_caps_at_an_hour()
    {
        Assert.True(WebhookDelivery.Backoff(1) < WebhookDelivery.Backoff(3));
        Assert.Equal(TimeSpan.FromMinutes(60), WebhookDelivery.Backoff(20));
    }

    // ---- dispatcher ----

    private sealed class StubHandler(HttpStatusCode code, Action<HttpRequestMessage>? capture = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            capture?.Invoke(request);
            return Task.FromResult(new HttpResponseMessage(code));
        }
    }

    private static WebhookSubscription Subscription() => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        Url = "https://hooks.example.com/3c",
        Secret = "s3cr3t",
        EventTypes = "order.confirmed",
        CreatedAt = Now,
    };

    [Fact]
    public async Task A_2xx_response_signs_the_request_and_marks_delivered()
    {
        string? signature = null;
        string? timestamp = null;
        var handler = new StubHandler(HttpStatusCode.OK, req =>
        {
            signature = req.Headers.GetValues(WebhookDispatcher.SignatureHeader).First();
            timestamp = req.Headers.GetValues(WebhookDispatcher.TimestampHeader).First();
        });
        const string payload = "{\"orderId\":\"abc\"}";
        var subscription = Subscription();
        var delivery = WebhookDelivery.Queue(subscription.TenantId, subscription.Id, "evt-1", "order.confirmed", Now);

        await new WebhookDispatcher(new HttpClient(handler), TimeProvider.System).DispatchAsync(subscription, delivery, payload, default);

        Assert.Equal(WebhookDeliveryStatus.Delivered, delivery.Status);
        var ts = long.Parse(timestamp!);
        Assert.Equal($"sha256={WebhookSignature.Compute(subscription.Secret, ts, payload)}", signature);
    }

    [Fact]
    public async Task A_server_error_records_a_retry()
    {
        var subscription = Subscription();
        var delivery = WebhookDelivery.Queue(subscription.TenantId, subscription.Id, "evt-1", "order.confirmed", Now);

        await new WebhookDispatcher(new HttpClient(new StubHandler(HttpStatusCode.InternalServerError)), TimeProvider.System)
            .DispatchAsync(subscription, delivery, "{}", default);

        Assert.Equal(WebhookDeliveryStatus.Retrying, delivery.Status);
        Assert.Equal(500, delivery.LastStatusCode);
        Assert.NotNull(delivery.NextAttemptAt);
    }

    [Fact]
    public void Subscription_event_matching_honours_wildcard_and_active_flag()
    {
        var sub = Subscription();
        Assert.True(sub.HandlesEvent("order.confirmed"));
        Assert.False(sub.HandlesEvent("order.cancelled"));

        sub.Active = false;
        Assert.False(sub.HandlesEvent("order.confirmed"));
    }
}
