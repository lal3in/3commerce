using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// A subscription holds its times at the precision Postgres stores them (whole microseconds). A Linux clock has 100 ns
/// ticks, which a timestamptz round trip drops — so without this a subscription started and renewed in one DbContext
/// keys its renewal <c>renew-{id}-{periodEnd:O}</c> differently from the same period reloaded, and the retry's
/// "same key + same pinned amount" guarantee breaks. The clock here is pinned to a sub-microsecond tick so the case is
/// deterministic on macOS too (whose clock only gives microseconds).
/// </summary>
public class SubscriptionStoredTimeTests
{
    // …54.1755249: the last tick digit (9 × 100 ns) is what Postgres drops.
    private static readonly DateTimeOffset LinuxNow = new DateTimeOffset(2026, 11, 27, 15, 47, 54, TimeSpan.Zero).AddTicks(1_755_249);
    private static readonly DateTimeOffset StoredNow = new DateTimeOffset(2026, 11, 27, 15, 47, 54, TimeSpan.Zero).AddTicks(1_755_240);

    [Fact]
    public void StoredTime_truncates_to_whole_microseconds()
    {
        Assert.Equal(StoredNow, StoredTime.Of(LinuxNow));
        Assert.Equal(StoredNow, StoredTime.Of(StoredNow)); // already stored precision: unchanged
    }

    [Fact]
    public void A_subscription_started_at_a_sub_microsecond_tick_holds_what_Postgres_will_store()
    {
        var sub = Subscription.Start(
            Guid.NewGuid(), Guid.NewGuid(), "buyer@example.com", Guid.NewGuid(), null, BillingPeriod.Monthly, 1500, "USD", LinuxNow);

        Assert.Equal(StoredNow, sub.CurrentPeriodStart);
        Assert.Equal(StoredNow.AddMonths(1), sub.CurrentPeriodEnd);
        Assert.Equal(StoredNow, sub.CreatedAt);
        Assert.Equal(StoredNow, sub.UpdatedAt);
        var first = Assert.Single(sub.Renewals);
        Assert.Equal(StoredNow, first.PeriodStart);
        Assert.Equal(StoredNow.AddMonths(1), first.PeriodEnd);
        Assert.Equal(StoredNow, first.RecordedAt);

        // The renewal key built from this in-memory aggregate == the key built from the same row reloaded.
        Assert.Equal(
            $"renew-{sub.Id}-{StoredTime.Of(sub.CurrentPeriodEnd):O}",
            $"renew-{sub.Id}-{sub.CurrentPeriodEnd:O}");
    }

    [Fact]
    public void Renewing_dunning_and_cancelling_stamp_stored_precision_times()
    {
        var sub = Subscription.Start(
            Guid.NewGuid(), Guid.NewGuid(), "buyer@example.com", Guid.NewGuid(), null, BillingPeriod.Monthly, 1500, "USD", StoredNow);
        var later = LinuxNow.AddMonths(1);

        sub.MarkPastDue(later);
        Assert.Equal(StoredTime.Of(later), sub.UpdatedAt);

        sub.Renew(later);
        Assert.Equal(StoredTime.Of(later), sub.UpdatedAt);
        Assert.Equal(StoredTime.Of(later), sub.Renewals[^1].RecordedAt);
        Assert.Equal(StoredNow.AddMonths(2), sub.CurrentPeriodEnd);

        sub.Cancel(later);
        Assert.Equal(StoredTime.Of(later), sub.UpdatedAt);
    }

    [Fact]
    public void A_quantity_backfill_correction_stamps_a_stored_precision_time()
    {
        var sub = Subscription.Start(
            Guid.NewGuid(), Guid.NewGuid(), "buyer@example.com", Guid.NewGuid(), null, BillingPeriod.Monthly, 500, "USD", StoredNow);

        Assert.True(sub.CorrectLegacyQuantity(3, 4500, LinuxNow));
        Assert.Equal(StoredNow, sub.UpdatedAt);
    }

    /// <summary>
    /// The dunning clocks are stored too, and the retry schedule (and the next-retry date the customer's email states)
    /// is computed from them: a past-due subscription must answer the same in memory as reloaded.
    /// </summary>
    [Fact]
    public void Dunning_stamps_stored_precision_times_so_the_retry_schedule_survives_a_reload()
    {
        var sub = Subscription.Start(
            Guid.NewGuid(), Guid.NewGuid(), "buyer@example.com", Guid.NewGuid(), null, BillingPeriod.Monthly, 1500, "USD", StoredNow);
        var failure = LinuxNow.AddMonths(1);
        var stored = StoredTime.Of(failure);

        sub.KeyIssued(failure);
        Assert.Equal(stored, sub.UpdatedAt);

        sub.AttemptAnswered(failure);
        Assert.Equal(stored, sub.LastAttemptAt);
        Assert.Equal(stored, sub.UpdatedAt);

        sub.MarkPastDue(failure, "Failed");
        Assert.Equal(stored, sub.DunningStartedAt);
        Assert.Equal(stored.AddDays(1), sub.NextRetryAt);   // what the PaymentFailed email states == what a reload computes
        Assert.Equal(stored.AddDays(7), sub.CancelsAfter);

        sub.ChangeInstrument("cus_2", "pm_2", failure);
        Assert.Equal(stored, sub.UpdatedAt);
    }
}
