namespace ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

public interface IStreamReplayWatermarkStore
{
    public Task<long?> GetOffsetAsync(string topic, string consumerGroup, CancellationToken cancellationToken = default);

    public Task MarkOffsetAsync(string topic, string consumerGroup, long offset, CancellationToken cancellationToken = default);
}

public sealed class InMemoryStreamReplayWatermarkStore : IStreamReplayWatermarkStore
{
    private readonly Dictionary<(string Topic, string ConsumerGroup), long> offsets = [];
    private readonly Lock gate = new();

    public Task<long?> GetOffsetAsync(string topic, string consumerGroup, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return Task.FromResult(offsets.TryGetValue((topic, consumerGroup), out var offset) ? offset : (long?)null);
        }
    }

    public Task MarkOffsetAsync(string topic, string consumerGroup, long offset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            offsets[(topic, consumerGroup)] = offset;
        }

        return Task.CompletedTask;
    }
}
