using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;
using ThreeCommerce.Fulfillment.Infrastructure.Consumers;
using NotificationsOrderConfirmedConsumer = ThreeCommerce.Workers.Notifications.Consumers.OrderConfirmedConsumer;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill;

/// <summary>
/// Where a backfilled event goes. The queue names are DERIVED with the shared formatter from the real consumer types
/// (ADR-0060), never typed by hand, so the tool follows a rename.
/// </summary>
public static class BackfillQueues
{
    public const string BackfillHeader = "3c-backfill";
    public const string BackfillHeaderValue = "order-confirmed (ADR-0060)";

    public static string QueueName(BackfillTarget target) => target switch
    {
        BackfillTarget.Fulfillment => MassTransitExtensions.EndpointNameFormatter.Consumer<FulfillmentOrderConfirmedConsumer>(),
        BackfillTarget.Notifications => MassTransitExtensions.EndpointNameFormatter.Consumer<NotificationsOrderConfirmedConsumer>(),
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    /// <summary>A <c>queue:</c> address: <c>Send</c> to it reaches that one queue — no exchange fan-out, no other consumer.</summary>
    public static Uri Address(BackfillTarget target) => new($"queue:{QueueName(target)}");

    /// <summary>
    /// The RabbitMQ client connection name the target's process opens (MassTransit names it after the host process) —
    /// what the preflight requires every consumer of the queue to be.
    /// </summary>
    public static string ExpectedConnectionName(BackfillTarget target) => target switch
    {
        BackfillTarget.Fulfillment => "3commerce.Fulfillment.Api",
        BackfillTarget.Notifications => "3commerce.Workers.Notifications",
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    /// <summary>
    /// A deterministic message id per (target, order): a repeat send of the same order is the SAME message, so
    /// Fulfillment's inbox (while it still holds the id) drops it before the consumer runs.
    /// </summary>
    public static Guid MessageId(BackfillTarget target, Guid orderId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"order-confirmed-backfill:{target}:{orderId:D}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}

/// <summary>Sends rebuilt events point-to-point (<c>Send</c>, never <c>Publish</c>).</summary>
public sealed class BackfillSender(ISendEndpointProvider endpoints)
{
    public async Task<int> SendAsync(
        BackfillTarget target, IEnumerable<OrderConfirmed> messages, Action<int>? progress, CancellationToken ct)
    {
        var endpoint = await endpoints.GetSendEndpoint(BackfillQueues.Address(target));
        var sent = 0;
        foreach (var message in messages)
        {
            await endpoint.Send(message, context =>
            {
                context.MessageId = BackfillQueues.MessageId(target, message.OrderId);
                context.Headers.Set(BackfillQueues.BackfillHeader, BackfillQueues.BackfillHeaderValue);
            }, ct);
            sent++;
            progress?.Invoke(sent);
        }

        return sent;
    }
}

/// <summary>
/// Refuses to send unless the broker shows the fix (#288) live: the target queue exists, every one of its consumers
/// is the target's own process (no competing consumer left to steal a copy), and nothing is still queued from an
/// earlier run (a re-run must not select orders whose event is merely in flight).
/// </summary>
public sealed class BrokerPreflight(HttpClient management)
{
    public Task<IReadOnlyList<string>> CheckAsync(BackfillTarget target, CancellationToken ct) =>
        CheckAsync(BackfillQueues.QueueName(target), BackfillQueues.ExpectedConnectionName(target), ct);

    /// <summary>The same checks for any queue and the one process that must be its only consumer.</summary>
    public async Task<IReadOnlyList<string>> CheckAsync(string queue, string expected, CancellationToken ct)
    {
        var problems = new List<string>();

        using var response = await management.GetAsync($"api/queues/%2F/{Uri.EscapeDataString(queue)}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return [$"queue '{queue}' does not exist — {expected} is not running the build that consumes it (ADR-0060 / #288)"];
        }

        response.EnsureSuccessStatusCode();
        var info = await response.Content.ReadFromJsonAsync<QueueInfo>(ct)
            ?? throw new InvalidOperationException($"empty management response for queue '{queue}'");

        if (info.Consumers == 0)
        {
            problems.Add($"queue '{queue}' has no consumer — start {expected} first");
        }

        if (info.Messages > 0)
        {
            problems.Add($"queue '{queue}' still holds {info.Messages} message(s) — let them drain before (re-)running");
        }

        foreach (var connection in (info.ConsumerDetails ?? []).Select(c => c.ChannelDetails?.ConnectionName).Distinct())
        {
            var name = connection is null ? null : await ClientNameAsync(connection, ct);
            if (!string.Equals(name, expected, StringComparison.Ordinal))
            {
                problems.Add($"queue '{queue}' is consumed by '{name ?? "<unknown>"}', expected only '{expected}' — a competing consumer would take some of the sends");
            }
        }

        return problems;
    }

    private async Task<string?> ClientNameAsync(string connection, CancellationToken ct)
    {
        using var response = await management.GetAsync($"api/connections/{Uri.EscapeDataString(connection)}", ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var info = await response.Content.ReadFromJsonAsync<ConnectionInfo>(ct);
        return info?.ClientProperties?.ConnectionName;
    }

    private sealed record QueueInfo(
        [property: JsonPropertyName("consumers")] int Consumers,
        [property: JsonPropertyName("messages")] int Messages,
        [property: JsonPropertyName("consumer_details")] List<ConsumerDetail>? ConsumerDetails);

    private sealed record ConsumerDetail([property: JsonPropertyName("channel_details")] ChannelDetail? ChannelDetails);

    private sealed record ChannelDetail([property: JsonPropertyName("connection_name")] string? ConnectionName);

    private sealed record ConnectionInfo([property: JsonPropertyName("client_properties")] ClientProperties? ClientProperties);

    private sealed record ClientProperties([property: JsonPropertyName("connection_name")] string? ConnectionName);
}
