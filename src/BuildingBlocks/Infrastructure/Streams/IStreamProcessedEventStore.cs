namespace ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

public interface IStreamProcessedEventStore
{
    public Task<bool> HasProcessedAsync(Guid eventId, CancellationToken cancellationToken = default);

    public Task MarkProcessedAsync(Guid eventId, CancellationToken cancellationToken = default);
}

public sealed class InMemoryStreamProcessedEventStore : IStreamProcessedEventStore
{
    private readonly HashSet<Guid> processed = [];
    private readonly Lock gate = new();

    public Task<bool> HasProcessedAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return Task.FromResult(processed.Contains(eventId));
        }
    }

    public Task MarkProcessedAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            processed.Add(eventId);
        }

        return Task.CompletedTask;
    }
}
