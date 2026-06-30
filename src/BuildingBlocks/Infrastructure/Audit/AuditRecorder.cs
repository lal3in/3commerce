using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Streams;
using ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Audit;

/// <summary>The local audit log a service writes to (mt6_1). Authoritative; central projection is separate.</summary>
public interface IAuditStore
{
    public Task<AuditEntry?> LastAsync(Guid tenantId, CancellationToken ct);

    public void Add(AuditEntry entry);

    public Task<List<AuditEntry>> ChainAsync(Guid tenantId, CancellationToken ct);
}

/// <summary>
/// EF-backed local audit store (mt6_1). Works over any DbContext that maps <see cref="AuditEntry"/>;
/// register one per service as <c>EfAuditStore&lt;MyDbContext&gt;</c>. Append does NOT call SaveChanges —
/// the entry is added to the unit of work so it commits atomically with the mutation it records.
/// </summary>
public sealed class EfAuditStore<TContext>(TContext db) : IAuditStore
    where TContext : DbContext
{
    public Task<AuditEntry?> LastAsync(Guid tenantId, CancellationToken ct) =>
        db.Set<AuditEntry>().Where(e => e.TenantId == tenantId).OrderByDescending(e => e.Sequence).FirstOrDefaultAsync(ct);

    public void Add(AuditEntry entry) => db.Set<AuditEntry>().Add(entry);

    public Task<List<AuditEntry>> ChainAsync(Guid tenantId, CancellationToken ct) =>
        db.Set<AuditEntry>().AsNoTracking().Where(e => e.TenantId == tenantId).OrderBy(e => e.Sequence).ToListAsync(ct);
}

/// <summary>
/// Records local audit entries and verifies the tenant's chain (mt6_1). Hash-chained and append-only;
/// the entry is staged in the current unit of work so it commits with the change it describes.
/// </summary>
public sealed class AuditRecorder(IAuditStore store, TimeProvider clock, IPublishEndpoint? publisher = null, StreamOutboxStager? streamOutbox = null)
{
    public async Task<AuditEntry> RecordAsync(AuditDraft draft, CancellationToken ct)
    {
        var previous = await store.LastAsync(draft.TenantId, ct);
        var entry = AuditChain.Append(previous, draft, clock.GetUtcNow());
        store.Add(entry);

        // Project to the central Audit service when a publisher is wired (the bus's EF outbox commits it
        // atomically with the local entry). Tests construct without a publisher → no-op.
        if (publisher is not null)
        {
            await publisher.Publish(new AuditEntryRecorded(
                entry.TenantId, entry.Sequence, entry.OccurredAt, entry.ActorId, entry.ActorRole,
                entry.Action, entry.ResourceType, entry.ResourceId, entry.Outcome.ToString(), entry.Summary, entry.Hash), ct);
        }

        if (streamOutbox is not null)
        {
            var payload = new AuditEntryStreamPayload(
                entry.Sequence,
                entry.OccurredAt,
                entry.ActorId,
                entry.ActorRole,
                entry.Action,
                entry.ResourceType,
                entry.ResourceId,
                entry.Outcome.ToString(),
                entry.Summary,
                entry.Hash);
            var envelope = StreamEventEnvelope<AuditEntryStreamPayload>.Create(
                Guid.CreateVersion7(),
                "AuditEntryRecorded",
                1,
                entry.OccurredAt,
                "audit",
                entry.TenantId,
                entry.Id.ToString(),
                StreamPartitionKeys.Tenant(entry.TenantId),
                StreamPrivacyClass.Internal,
                payload);
            await streamOutbox.StageAsync(StreamTopics.AuditEntries, envelope, cancellationToken: ct);
        }

        return entry;
    }

    public async Task<AuditVerification> VerifyAsync(Guid tenantId, CancellationToken ct) =>
        AuditChain.Verify(await store.ChainAsync(tenantId, ct));
}
