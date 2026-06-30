using System.Diagnostics.Metrics;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

public static class StreamMetrics
{
    public const string MeterName = "3commerce.streams";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Published = Meter.CreateCounter<long>("stream_events_published_total");
    private static readonly Counter<long> Consumed = Meter.CreateCounter<long>("stream_events_consumed_total");
    private static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>("stream_events_dead_lettered_total");
    private static readonly Counter<long> RelayPublished = Meter.CreateCounter<long>("stream_outbox_relay_published_total");
    private static readonly Counter<long> RelayFailed = Meter.CreateCounter<long>("stream_outbox_relay_failed_total");

    public static void RecordPublished(string topic) => Published.Add(1, new KeyValuePair<string, object?>("topic", topic));

    public static void RecordConsumed(string topic, string result) => Consumed.Add(1, new KeyValuePair<string, object?>("topic", topic), new KeyValuePair<string, object?>("result", result));

    public static void RecordDeadLettered(string topic, string errorType) => DeadLettered.Add(1, new KeyValuePair<string, object?>("topic", topic), new KeyValuePair<string, object?>("error_type", errorType));

    public static void RecordRelayPublished(string topic) => RelayPublished.Add(1, new KeyValuePair<string, object?>("topic", topic));

    public static void RecordRelayFailed(string topic) => RelayFailed.Add(1, new KeyValuePair<string, object?>("topic", topic));
}
