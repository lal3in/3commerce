using System.Net.Http.Headers;
using System.Text;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Webhooks;

/// <summary>
/// Delivers a webhook (mt6_6): POSTs the JSON payload to the subscription's endpoint with a signed,
/// timestamped header set, and records the outcome on the <see cref="WebhookDelivery"/> (success →
/// Delivered; non-2xx or transport error → Retrying/Exhausted with backoff). No throw — the delivery
/// row carries the result so a sweep can retry.
/// </summary>
public sealed class WebhookDispatcher(HttpClient http, TimeProvider clock)
{
    public const string SignatureHeader = "X-Webhook-Signature";
    public const string TimestampHeader = "X-Webhook-Timestamp";
    public const string IdHeader = "X-Webhook-Id";
    public const string EventHeader = "X-Webhook-Event";

    public async Task DispatchAsync(WebhookSubscription subscription, WebhookDelivery delivery, string payload, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var timestamp = now.ToUnixTimeSeconds();
        var signature = WebhookSignature.Compute(subscription.Secret, timestamp, payload);

        using var request = new HttpRequestMessage(HttpMethod.Post, subscription.Url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(SignatureHeader, $"sha256={signature}");
        request.Headers.TryAddWithoutValidation(TimestampHeader, timestamp.ToString());
        request.Headers.TryAddWithoutValidation(IdHeader, delivery.EventId);
        request.Headers.TryAddWithoutValidation(EventHeader, delivery.EventType);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("3commerce-webhooks", "1.0"));

        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                delivery.RecordSuccess((int)response.StatusCode, now);
            }
            else
            {
                delivery.RecordFailure((int)response.StatusCode, $"HTTP {(int)response.StatusCode}", now);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            delivery.RecordFailure(null, ex.Message, now);
        }
    }
}
