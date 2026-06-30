namespace ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

public interface IStreamEventHandler<TPayload>
{
    public Task HandleAsync(StreamConsumedEvent<TPayload> message, CancellationToken cancellationToken = default);
}
