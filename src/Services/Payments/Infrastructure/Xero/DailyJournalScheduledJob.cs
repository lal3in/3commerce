using ThreeCommerce.BuildingBlocks.Infrastructure.Scheduling;

namespace ThreeCommerce.Payments.Infrastructure.Xero;

/// <summary>
/// Runs the daily Xero journal post on a cron (mt6_3) — the scheduled half of <see cref="DailyJournalJob"/>,
/// which was previously only callable from the admin endpoint. Posts the prior UTC day; idempotent per date.
/// </summary>
public sealed class DailyJournalScheduledJob(DailyJournalJob job, TimeProvider clock) : IScheduledJob
{
    public string Name => "daily-journal";

    // 02:00 UTC daily — after the day has fully closed.
    public string CronSchedule => "0 0 2 * * ?";

    public Task ExecuteAsync(CancellationToken ct)
    {
        var yesterday = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime.AddDays(-1));
        return job.RunForAsync(yesterday, ct);
    }
}
