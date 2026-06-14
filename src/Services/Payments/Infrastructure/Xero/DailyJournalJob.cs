using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ThreeCommerce.Payments.Domain.Xero;

namespace ThreeCommerce.Payments.Infrastructure.Xero;

/// <summary>
/// Posts yesterday's ledger to Xero as one balanced summary journal (ADR-0017). Idempotent
/// per date. Wired as a MassTransit job/recurring schedule would be ideal; v1 exposes the
/// core as a callable method so the admin endpoint and a future cron can both invoke it.
/// </summary>
public sealed class DailyJournalJob(IServiceScopeFactory scopeFactory, IXeroClient xero, ILogger<DailyJournalJob> logger)
{
    public async Task<SyncRunStatus> RunForAsync(DateOnly date, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        var reference = $"daily:{date:yyyy-MM-dd}";
        if (await db.Set<SyncRun>().AnyAsync(s => s.Reference == reference, ct))
        {
            return SyncRunStatus.Skipped; // already posted for this date
        }

        var start = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var end = start.AddDays(1);
        var lines = await db.JournalLines
            .Where(l => db.JournalEntries.Any(e => e.Id == l.EntryId && e.CreatedAt >= start && e.CreatedAt < end))
            .ToListAsync(ct);

        var run = new SyncRun { Id = Guid.CreateVersion7(), Reference = reference, Status = SyncRunStatus.Pending, CreatedAt = DateTimeOffset.UtcNow };
        db.Add(run);

        var journal = XeroJournalBuilder.BuildDaily(date, lines);
        if (journal is null)
        {
            run.Status = SyncRunStatus.Skipped; // zero-activity day
            await db.SaveChangesAsync(ct);
            return SyncRunStatus.Skipped;
        }

        try
        {
            run.XeroJournalId = await xero.PostManualJournalAsync(journal, ct);
            run.Status = SyncRunStatus.Posted;
            logger.LogInformation("Posted daily Xero journal for {Date}", date);
        }
        catch (Exception ex)
        {
            run.Status = SyncRunStatus.Failed;
            run.Error = ex.Message;
        }

        await db.SaveChangesAsync(ct);
        return run.Status;
    }
}
