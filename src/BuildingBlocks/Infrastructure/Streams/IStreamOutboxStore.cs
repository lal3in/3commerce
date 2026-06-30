namespace ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

public interface IStreamOutboxStore
{
    public Task AddAsync(StreamOutboxMessage message, CancellationToken cancellationToken = default);

    public Task<IReadOnlyList<StreamOutboxMessage>> ClaimBatchAsync(int batchSize, DateTimeOffset now, CancellationToken cancellationToken = default);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
