using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Audit.Domain;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;

namespace ThreeCommerce.Audit.Infrastructure;

/// <summary>
/// Projects a service's audit entry into the central searchable store (mt6_1). Idempotent per (tenant, hash):
/// the hash is the entry's tamper-evident content digest, unique across every producing service — unlike the
/// per-service Sequence, which restarts at 1 in each source (and is always 1 for publish-only producers), so
/// deduping on sequence would collapse distinct entries from different services. The bus inbox is the primary
/// redelivery guard; this is a secondary check.
/// </summary>
public sealed class AuditEntryRecordedConsumer(AuditDbContext db) : IConsumer<AuditEntryRecorded>
{
    public async Task Consume(ConsumeContext<AuditEntryRecorded> context)
    {
        var m = context.Message;
        if (await db.AuditEntries.AnyAsync(e => e.TenantId == m.TenantId && e.Hash == m.Hash, context.CancellationToken))
        {
            return;
        }

        db.AuditEntries.Add(new AuditProjection
        {
            Id = Guid.CreateVersion7(),
            TenantId = m.TenantId,
            Sequence = m.Sequence,
            OccurredAt = m.OccurredAt,
            ActorId = m.ActorId,
            ActorRole = m.ActorRole,
            Action = m.Action,
            ResourceType = m.ResourceType,
            ResourceId = m.ResourceId,
            Outcome = m.Outcome,
            Summary = m.Summary,
            Hash = m.Hash,
        });
        await db.SaveChangesAsync(context.CancellationToken);
    }
}
