using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ThreeCommerce.Admin.Services;

/// <summary>
/// Live message-bus stats for Mission Control (def_6 / mt6_14), read from the RabbitMQ
/// management API — the operational source of truth for queue depth/rates/consumers. Read-only
/// and best-effort: an unreachable management API degrades to a hint, never an error page.
/// MassTransit convention: `*_error` / `*_skipped` queues are the dead-letter surface.
/// </summary>
public sealed class BusStatsService(IHttpClientFactory factory, IConfiguration config)
{
    public sealed record QueueStat(string Name, long Ready, long Unacked, long Consumers, double PublishRate, bool IsDeadLetter);

    public sealed record BusSnapshot(
        int Queues, long Consumers, long Ready, long Unacked, long DeadLettered, List<QueueStat> TopQueues, List<QueueStat> DeadLetterQueues);

    public async Task<BusSnapshot?> GetSnapshotAsync(CancellationToken ct = default)
    {
        try
        {
            var client = factory.CreateClient("rabbitmq-mgmt");
            var user = config["MessageBus:ManagementUser"] ?? "guest";
            var password = config["MessageBus:ManagementPassword"] ?? "guest";
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/queues?disable_stats=false");
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var queues = new List<QueueStat>();
            foreach (var q in doc.RootElement.EnumerateArray())
            {
                var name = q.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                queues.Add(new QueueStat(
                    name,
                    Ready: Int64Of(q, "messages_ready"),
                    Unacked: Int64Of(q, "messages_unacknowledged"),
                    Consumers: Int64Of(q, "consumers"),
                    PublishRate: PublishRateOf(q),
                    IsDeadLetter: name.EndsWith("_error", StringComparison.Ordinal) || name.EndsWith("_skipped", StringComparison.Ordinal)));
            }

            var deadLetter = queues.Where(q => q.IsDeadLetter && q.Ready + q.Unacked > 0)
                .OrderByDescending(q => q.Ready).ToList();
            return new BusSnapshot(
                Queues: queues.Count,
                Consumers: queues.Sum(q => q.Consumers),
                Ready: queues.Sum(q => q.Ready),
                Unacked: queues.Sum(q => q.Unacked),
                DeadLettered: queues.Where(q => q.IsDeadLetter).Sum(q => q.Ready + q.Unacked),
                TopQueues: queues.Where(q => !q.IsDeadLetter)
                    .OrderByDescending(q => q.Ready + q.Unacked).ThenByDescending(q => q.PublishRate).Take(10).ToList(),
                DeadLetterQueues: deadLetter);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or UriFormatException)
        {
            return null; // degrade to the "unreachable" hint
        }
    }

    private static long Int64Of(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;

    private static double PublishRateOf(JsonElement queue) =>
        queue.TryGetProperty("message_stats", out var stats)
        && stats.TryGetProperty("publish_details", out var details)
        && details.TryGetProperty("rate", out var rate)
        && rate.ValueKind == JsonValueKind.Number
            ? rate.GetDouble()
            : 0;
}
