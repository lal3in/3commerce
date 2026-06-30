using ThreeCommerce.BuildingBlocks.Contracts.Streams;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

public interface IStreamEventProducer
{
    public Task<StreamPublishResult> PublishAsync<TPayload>(
        string topic,
        StreamEventEnvelope<TPayload> envelope,
        CancellationToken cancellationToken = default);
}
