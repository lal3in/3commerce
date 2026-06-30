using System.Collections.Concurrent;
using System.Text.Json;
using ThreeCommerce.BuildingBlocks.Contracts.Streams;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

public sealed record PublishedStreamEvent(string Topic, string Key, string EventJson, IReadOnlyDictionary<string, string> Headers);

public sealed class FakeStreamEventProducer : IStreamEventProducer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentQueue<PublishedStreamEvent> events = new();

    public IReadOnlyCollection<PublishedStreamEvent> Published => events.ToArray();

    public Task<StreamPublishResult> PublishAsync<TPayload>(
        string topic,
        StreamEventEnvelope<TPayload> envelope,
        CancellationToken cancellationToken = default)
    {
        StreamPrivacyGuard.ValidateForPublication(topic, envelope);
        cancellationToken.ThrowIfCancellationRequested();

        var headers = StreamHeaders.FromEnvelope(envelope);
        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        events.Enqueue(new PublishedStreamEvent(topic, envelope.PartitionKey, json, headers));
        StreamMetrics.RecordPublished(topic);
        return Task.FromResult(new StreamPublishResult(topic, envelope.PartitionKey, events.Count - 1));
    }
}
