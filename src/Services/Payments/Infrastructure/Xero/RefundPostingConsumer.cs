using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Payments.Domain.Xero;

namespace ThreeCommerce.Payments.Infrastructure.Xero;

/// <summary>
/// Posts each completed refund to Xero individually (ADR-0017). Idempotent by SyncRun
/// reference. Async/off the refund critical path — a Xero failure never blocks refunds.
/// </summary>
public sealed class RefundPostingConsumer(
    PaymentsDbContext db, IXeroClient xero, TimeProvider time) : IConsumer<RefundCompleted>
{
    public async Task Consume(ConsumeContext<RefundCompleted> context)
    {
        var reference = $"refund:{context.Message.RefundId}";
        if (await db.Set<SyncRun>().AnyAsync(s => s.Reference == reference, context.CancellationToken))
        {
            return;
        }

        var lines = await db.JournalLines
            .Where(l => db.JournalEntries.Any(e => e.Id == l.EntryId && e.Reference == context.Message.RefundId.ToString()))
            .ToListAsync(context.CancellationToken);

        var run = new SyncRun { Id = Guid.CreateVersion7(), Reference = reference, Status = SyncRunStatus.Pending, CreatedAt = time.GetUtcNow() };
        db.Add(run);

        try
        {
            var journal = XeroJournalBuilder.BuildRefund(
                context.Message.RefundId, DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime), lines);
            run.XeroJournalId = await xero.PostManualJournalAsync(journal, context.CancellationToken);
            run.Status = SyncRunStatus.Posted;
        }
        catch (Exception ex)
        {
            run.Status = SyncRunStatus.Failed;
            run.Error = ex.Message;
        }

        await db.SaveChangesAsync(context.CancellationToken);
    }
}
