namespace ThreeCommerce.Payments.Domain;

/// <summary>
/// A storefront's daily auto-billing schedule (jobmgr_2). The auto-renew sweep runs on a fine cadence and,
/// for each storefront, fires exactly once per day once its <see cref="DailyRunTime"/> (server time) has
/// arrived — <see cref="LastRunOn"/> guards against a second run the same day. Defaults to 12:00:00, enabled.
/// </summary>
public sealed class StorefrontBillingSchedule
{
    public Guid StorefrontId { get; init; }
    public TimeOnly DailyRunTime { get; private set; } = new(12, 0, 0);
    public bool Enabled { get; private set; } = true;
    public DateOnly? LastRunOn { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private StorefrontBillingSchedule() { }

    public static StorefrontBillingSchedule CreateDefault(Guid storefrontId, DateTimeOffset now) =>
        new() { StorefrontId = storefrontId, UpdatedAt = now };

    /// <summary>Enabled, its daily time has passed today, and it hasn't already run today.</summary>
    public bool ShouldRunNow(DateOnly today, TimeOnly now) =>
        Enabled && now >= DailyRunTime && LastRunOn != today;

    public void MarkRan(DateOnly today, DateTimeOffset now)
    {
        LastRunOn = today;
        UpdatedAt = now;
    }

    public void Configure(TimeOnly dailyRunTime, bool enabled, DateTimeOffset now)
    {
        DailyRunTime = dailyRunTime;
        Enabled = enabled;
        UpdatedAt = now;
    }
}
