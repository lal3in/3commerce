using ThreeCommerce.BuildingBlocks.Infrastructure.Scheduling;

namespace ThreeCommerce.Payments.Infrastructure.Scheduling;

/// <summary>
/// Auto-renews due subscriptions on each storefront's daily schedule (jobmgr_2). Runs every 15 minutes so a
/// storefront's window is caught promptly; the sweep itself fires at most once per storefront per day (once
/// its configured time has passed), charging due renewals off-session. Idempotent; appears in the Scheduled-
/// Job Manager. Gated by Scheduling:Enabled.
/// </summary>
public sealed class SubscriptionAutoRenewJob(SubscriptionService subscriptions) : IScheduledJob
{
    public string Name => "subscription-auto-renew";

    // Every 15 minutes — each storefront still renews only once/day, when its DailyRunTime has passed.
    public string CronSchedule => "0 0/15 * * * ?";

    public Task ExecuteAsync(CancellationToken ct) => subscriptions.AutoRenewDueAsync(ct);
}
