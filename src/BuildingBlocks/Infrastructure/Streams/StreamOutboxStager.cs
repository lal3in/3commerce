using System.Text.Json;
using ThreeCommerce.BuildingBlocks.Contracts.Streams;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

public sealed class StreamOutboxStager
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IStreamOutboxStore store;

    public StreamOutboxStager(IStreamOutboxStore store)
    {
        this.store = store;
    }

    public async Task<StreamOutboxMessage> StageAsync<TPayload>(
        string topic,
        StreamEventEnvelope<TPayload> envelope,
        DateTimeOffset? availableAt = null,
        CancellationToken cancellationToken = default)
    {
        StreamPrivacyGuard.ValidateForPublication(topic, envelope);
        var message = StreamOutboxMessage.Stage(
            topic,
            envelope.PartitionKey,
            envelope.EventType,
            envelope.EventVersion,
            envelope.TenantId,
            JsonSerializer.Serialize(envelope, JsonOptions),
            JsonSerializer.Serialize(StreamHeaders.FromEnvelope(envelope), JsonOptions),
            envelope.OccurredAt,
            availableAt);

        await store.AddAsync(message, cancellationToken);
        return message;
    }
}
