using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Audit.Domain;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;

namespace ThreeCommerce.Audit.Infrastructure;

/// <summary>Projects a service's audit entry into the central searchable store (mt6_1). Idempotent per (tenant, sequence).</summary>
public sealed class AuditEntryRecordedConsumer(AuditDbContext db) : IConsumer<AuditEntryRecorded>
{
    public async Task Consume(ConsumeContext<AuditEntryRecorded> context)
    {
        var m = context.Message;
        if (await db.AuditEntries.AnyAsync(e => e.TenantId == m.TenantId && e.Sequence == m.Sequence, context.CancellationToken))
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
