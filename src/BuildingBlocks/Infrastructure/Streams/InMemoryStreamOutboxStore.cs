namespace ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

public sealed class InMemoryStreamOutboxStore : IStreamOutboxStore
{
    private readonly List<StreamOutboxMessage> messages = [];
    private readonly Lock gate = new();

    public IReadOnlyList<StreamOutboxMessage> Messages
    {
        get
        {
            lock (gate)
            {
                return messages.ToArray();
            }
        }
    }

    public Task AddAsync(StreamOutboxMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            messages.Add(message);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StreamOutboxMessage>> ClaimBatchAsync(int batchSize, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<StreamOutboxMessage>>(messages
                .Where(x => x.PublishedAt is null && x.AvailableAt <= now)
                .OrderBy(x => x.AvailableAt)
                .ThenBy(x => x.Id)
                .Take(batchSize)
                .ToArray());
        }
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
