using ThreeCommerce.BuildingBlocks.Contracts.Streams;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

public sealed record StreamConsumedEvent<TPayload>(
    string Topic,
    string Key,
    long Offset,
    StreamEventEnvelope<TPayload> Envelope,
    IReadOnlyDictionary<string, string> Headers);
