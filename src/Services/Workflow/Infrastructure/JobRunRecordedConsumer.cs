using MassTransit;
using ThreeCommerce.BuildingBlocks.Infrastructure.Scheduling;
using ThreeCommerce.Workflow.Domain;

namespace ThreeCommerce.Workflow.Infrastructure;

/// <summary>Projects a scheduled-job run into the central Workflow store (mt6_3). Idempotent (upsert by Id).</summary>
public sealed class JobRunRecordedConsumer(WorkflowDbContext db) : IConsumer<JobRunRecorded>
{
    public async Task Consume(ConsumeContext<JobRunRecorded> context)
    {
        var m = context.Message;
        var run = await db.Runs.FindAsync([m.Id], context.CancellationToken);
        if (run is null)
        {
            db.Runs.Add(new WorkflowRun { Id = m.Id, JobName = m.JobName, Status = m.Status, StartedAt = m.StartedAt, CompletedAt = m.CompletedAt, Error = m.Error });
        }
        else
        {
            run.Status = m.Status;
            run.CompletedAt = m.CompletedAt;
            run.Error = m.Error;
        }

        await db.SaveChangesAsync(context.CancellationToken);
    }
}
