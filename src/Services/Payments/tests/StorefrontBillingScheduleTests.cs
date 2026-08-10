using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// Per-storefront auto-billing schedule gate (jobmgr_2): a storefront renews once per day, only after its
/// configured daily time has passed, and never twice the same day.
/// </summary>
public class StorefrontBillingScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 8, 10);

    private static StorefrontBillingSchedule Default() => StorefrontBillingSchedule.CreateDefault(Guid.NewGuid(), Now);

    [Fact]
    public void Default_is_noon_enabled_never_run()
    {
        var s = Default();
        Assert.Equal(new TimeOnly(12, 0, 0), s.DailyRunTime);
        Assert.True(s.Enabled);
        Assert.Null(s.LastRunOn);
    }

    [Fact]
    public void Should_run_once_the_daily_time_has_passed_today()
    {
        var s = Default();
        Assert.False(s.ShouldRunNow(Today, new TimeOnly(11, 59, 0))); // before noon
        Assert.True(s.ShouldRunNow(Today, new TimeOnly(12, 0, 0)));   // at noon
        Assert.True(s.ShouldRunNow(Today, new TimeOnly(13, 30, 0)));  // after noon
    }

    [Fact]
    public void Does_not_run_twice_the_same_day()
    {
        var s = Default();
        Assert.True(s.ShouldRunNow(Today, new TimeOnly(12, 0, 0)));

        s.MarkRan(Today, Now);
        Assert.False(s.ShouldRunNow(Today, new TimeOnly(13, 0, 0))); // already ran today

        Assert.True(s.ShouldRunNow(Today.AddDays(1), new TimeOnly(12, 0, 0))); // next day runs again
    }

    [Fact]
    public void A_disabled_schedule_never_runs()
    {
        var s = Default();
        s.Configure(new TimeOnly(0, 0, 0), enabled: false, Now);

        Assert.False(s.ShouldRunNow(Today, new TimeOnly(23, 59, 0)));
    }

    [Fact]
    public void Configure_updates_the_time_and_switch()
    {
        var s = Default();
        s.Configure(new TimeOnly(9, 30, 0), enabled: true, Now);

        Assert.Equal(new TimeOnly(9, 30, 0), s.DailyRunTime);
        Assert.True(s.ShouldRunNow(Today, new TimeOnly(9, 30, 0)));
        Assert.False(s.ShouldRunNow(Today, new TimeOnly(9, 0, 0)));
    }
}
