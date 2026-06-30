using Microsoft.EntityFrameworkCore;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

public sealed class EfStreamOutboxStore<TDbContext> : IStreamOutboxStore
    where TDbContext : DbContext
{
    private readonly TDbContext db;

    public EfStreamOutboxStore(TDbContext db)
    {
        this.db = db;
    }

    public async Task AddAsync(StreamOutboxMessage message, CancellationToken cancellationToken = default) =>
        await db.Set<StreamOutboxMessage>().AddAsync(message, cancellationToken);

    public async Task<IReadOnlyList<StreamOutboxMessage>> ClaimBatchAsync(int batchSize, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "Batch size must be positive.");

        return await db.Set<StreamOutboxMessage>()
            .Where(x => x.PublishedAt == null && x.AvailableAt <= now)
            .OrderBy(x => x.AvailableAt)
            .ThenBy(x => x.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) => db.SaveChangesAsync(cancellationToken);
}
