using MassTransit;
using ThreeCommerce.BuildingBlocks.Infrastructure.Scheduling;
using ThreeCommerce.Workflow.Domain;

namespace ThreeCommerce.Workflow.Infrastructure;

/// <summary>Projects a job's current schedule/state into the central registry (Scheduled-Job Manager).
/// Upsert by (Service, Name) — the latest descriptor wins.</summary>
public sealed class ScheduledJobDescriptorConsumer(WorkflowDbContext db) : IConsumer<ScheduledJobDescriptor>
{
    public async Task Consume(ConsumeContext<ScheduledJobDescriptor> context)
    {
        var m = context.Message;
        var entry = await db.ScheduledJobs.FindAsync([m.Service, m.Name], context.CancellationToken);
        if (entry is null)
        {
            db.ScheduledJobs.Add(new ScheduledJobEntry
            {
                Service = m.Service,
                Name = m.Name,
                Cron = m.Cron,
                Paused = m.Paused,
                NextFireUtc = m.NextFireUtc,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            entry.Cron = m.Cron;
            entry.Paused = m.Paused;
            entry.NextFireUtc = m.NextFireUtc;
            entry.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(context.CancellationToken);
    }
}
