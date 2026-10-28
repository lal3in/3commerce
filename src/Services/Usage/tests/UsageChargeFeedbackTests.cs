using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.BuildingBlocks.Contracts.Tax.Facts;
using ThreeCommerce.Usage.Domain;
using ThreeCommerce.Usage.Infrastructure;

namespace ThreeCommerce.Usage.Tests;

/// <summary>
/// usage_charge_failure_feedback (owner decision 2026-10-27): Usage credits a prepaid auto-load / marks an overage billed
/// when it PUBLISHES the charge, so Payments' outcome must reach back. A declined charge takes back the auto-load credit
/// (once), holds the balance's billing (no further auto-load while it is retried; billable usage suspended once it is
/// abandoned), and a charge that settles after all re-grants the credit and releases the hold. An overage is never
/// billed twice — and its reference is unique per period, so a repeated overage level is still charged.
/// </summary>
public class UsageChargeFeedbackTests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);
    private const string Email = "metered@example.com";

    [Fact]
    public async Task A_declined_auto_load_takes_back_its_credit_once_and_stops_further_top_ups()
    {
        var (db, usage, published) = Setup();
        await ProvisionAutoLoadAsync(usage, published);

        await usage.RecordAsync(TenantId, Email, MeterType.Request, 95, "u1", default); // credit 100 → 5 ≤ 10: top up 100
        var charge = Assert.IsType<UsageAutoLoadCharge>(Assert.Single(published));
        Assert.Equal((100L, 2_000L, 20L), (charge.ReloadQuantity, charge.ChargeMinor, charge.UnitPriceMinor));
        Assert.Equal(105, (await BalanceAsync(db)).PrepaidRemainingQuantity);   // credited optimistically

        var failed = Failed(charge.Reference, UsageChargeKind.AutoLoad, 100, final: false);
        await usage.ApplyFailedAsync(failed, default);
        await usage.ApplyFailedAsync(failed, default);                          // redelivered
        await usage.ApplyFailedAsync(failed with { Attempt = 1 }, default);     // the day-1 retry failed too

        var balance = await BalanceAsync(db);
        Assert.Equal(5, balance.PrepaidRemainingQuantity);                      // taken back exactly once
        Assert.Equal(UsagePaymentHold.Unpaid, balance.PaymentHold);
        Assert.Equal(UsageChargeState.Unpaid, (await db.UsageCharges.SingleAsync(c => c.Reference == charge.Reference)).State);

        await usage.RecordAsync(TenantId, Email, MeterType.Request, 5, "u2", default); // at the threshold again
        Assert.Single(published);                                               // no new top-up on a declining card
        await Assert.ThrowsAsync<UsageRuleException>(() => usage.RecordAsync(TenantId, Email, MeterType.Request, 1, "u3", default));
    }

    [Fact]
    public async Task An_auto_load_that_settles_after_a_decline_re_grants_its_credit_and_releases_the_hold()
    {
        var (db, usage, published) = Setup();
        await ProvisionAutoLoadAsync(usage, published);
        await usage.RecordAsync(TenantId, Email, MeterType.Request, 95, "u1", default);
        var reference = Assert.IsType<UsageAutoLoadCharge>(Assert.Single(published)).Reference;
        await usage.ApplyFailedAsync(Failed(reference, UsageChargeKind.AutoLoad, 100, final: false), default);

        var settled = Settled(reference, UsageChargeKind.AutoLoad, 100);
        await usage.ApplySettledAsync(settled, default);
        await usage.ApplySettledAsync(settled, default);                         // redelivered
        await usage.ApplyFailedAsync(Failed(reference, UsageChargeKind.AutoLoad, 100, final: true), default); // stale: ignored

        var balance = await BalanceAsync(db);
        Assert.Equal(105, balance.PrepaidRemainingQuantity);                    // re-granted once
        Assert.Equal(UsagePaymentHold.None, balance.PaymentHold);
        Assert.Equal(UsageChargeState.Settled, (await db.UsageCharges.SingleAsync(c => c.Reference == reference)).State);
    }

    [Fact]
    public async Task An_abandoned_charge_suspends_billable_usage_but_not_the_allowance()
    {
        var (db, usage, published) = Setup();
        await usage.ProvisionAsync(TenantId, Email, MeterType.Request, 100, overageAllowed: true, 20, "USD", null, default);
        await usage.RecordAsync(TenantId, Email, MeterType.Request, 130, "u1", default);
        var balanceId = (await BalanceAsync(db)).Id;
        await usage.BillOverageAsync(TenantId, balanceId, default);
        var overage = Assert.IsType<UsageOverageCharge>(Assert.Single(published));

        await usage.ApplyFailedAsync(Failed(overage.Reference, UsageChargeKind.Overage, 30, final: false), default);
        Assert.Equal(UsagePaymentHold.Unpaid, (await BalanceAsync(db)).PaymentHold);
        await usage.BillOverageAsync(TenantId, balanceId, default);
        Assert.Single(published);                                               // an unpaid overage is never billed twice

        await usage.ApplyFailedAsync(Failed(overage.Reference, UsageChargeKind.Overage, 30, final: true), default);
        Assert.Equal(UsagePaymentHold.Suspended, (await BalanceAsync(db)).PaymentHold);
        Assert.Equal(UsageChargeState.Abandoned, (await db.UsageCharges.SingleAsync()).State);
        var refused = await Assert.ThrowsAsync<UsageRuleException>(() => usage.RecordAsync(TenantId, Email, MeterType.Request, 1, "u2", default));
        Assert.Contains("unpaid", refused.Message, StringComparison.Ordinal);

        await usage.ApplySettledAsync(Settled(overage.Reference, UsageChargeKind.Overage, 30), default); // paid after all
        Assert.Equal(UsagePaymentHold.None, (await BalanceAsync(db)).PaymentHold);
        await usage.RecordAsync(TenantId, Email, MeterType.Request, 1, "u3", default);
    }

    [Fact]
    public void The_hold_follows_the_worst_open_charge()
    {
        Assert.Equal(UsagePaymentHold.None, UsageCharge.HoldFor([UsageChargeState.Open, UsageChargeState.Settled]));
        Assert.Equal(UsagePaymentHold.Unpaid, UsageCharge.HoldFor([UsageChargeState.Settled, UsageChargeState.Unpaid]));
        Assert.Equal(UsagePaymentHold.Suspended, UsageCharge.HoldFor([UsageChargeState.Unpaid, UsageChargeState.Abandoned]));
    }

    [Fact]
    public void Accepting_usage_under_a_hold()
    {
        var balance = UsageBalance.Create(TenantId, Email, MeterType.Request, Now);
        balance.Provision(10, overageAllowed: true, 20, "USD", null, Now);
        balance.ConfigureAutoLoad(true, 100, 100, Now);
        balance.ApplyAutoLoad(Now);                                              // 100 prepaid — at the threshold
        balance.Add(10, Now);                                                    // the allowance is used up
        Assert.True(balance.ShouldAutoLoad());                                   // nothing held: it would top up

        balance.SetPaymentHold(UsagePaymentHold.Suspended, Now);
        Assert.True(balance.CanAccept(100));                                     // the prepaid credit still covers it
        Assert.False(balance.CanAccept(101));                                    // beyond it: suspended, even with overage allowed
        Assert.False(balance.ShouldAutoLoad());

        balance.SetPaymentHold(UsagePaymentHold.Unpaid, Now);
        Assert.True(balance.CanAccept(101));                                     // overage allowed still accrues (billed in arrears)
        balance.Provision(10, overageAllowed: false, 20, "USD", null, Now);
        Assert.False(balance.CanAccept(101));                                    // auto-load no longer grants more while unpaid

        balance.SetPaymentHold(UsagePaymentHold.None, Now);
        Assert.True(balance.CanAccept(101));                                     // unchanged when nothing is held
    }

    [Fact]
    public async Task An_overage_reference_is_unique_per_period_so_a_repeated_level_is_still_charged()
    {
        var (db, usage, published) = Setup();
        await usage.ProvisionAsync(TenantId, Email, MeterType.Request, 10, overageAllowed: true, 20, "USD", Now.AddDays(-1), default);
        var balance = await BalanceAsync(db);
        await usage.RecordAsync(TenantId, Email, MeterType.Request, 15, "p1", default);
        await usage.CloseDuePeriodsAsync(default);                               // bills 5 and rolls
        await usage.RecordAsync(TenantId, Email, MeterType.Request, 15, "p2", default);
        await usage.BillOverageAsync(TenantId, balance.Id, default);             // the same level (5) in the next period

        var references = published.OfType<UsageOverageCharge>().Select(c => c.Reference).ToList();
        Assert.Equal(2, references.Count);
        Assert.Equal(2, references.Distinct().Count());
        Assert.Equal(2, await db.UsageCharges.CountAsync());
    }

    [Fact]
    public async Task An_outcome_for_a_charge_published_before_charges_were_recorded_still_applies()
    {
        var (db, usage, published) = Setup();
        await ProvisionAutoLoadAsync(usage, published);
        await usage.RecordAsync(TenantId, Email, MeterType.Request, 95, "u1", default);
        db.UsageCharges.RemoveRange(db.UsageCharges);                            // as if published by the previous version
        await db.SaveChangesAsync();

        await usage.ApplyFailedAsync(Failed("autoload-legacy-1", UsageChargeKind.AutoLoad, 100, final: false), default);

        Assert.Equal(5, (await BalanceAsync(db)).PrepaidRemainingQuantity);
        Assert.Equal(UsageChargeState.Unpaid, (await db.UsageCharges.SingleAsync()).State);
    }

    // ---- Helpers --------------------------------------------------------------------------------------

    private static (UsageDbContext Db, UsageService Usage, List<object> Published) Setup()
    {
        var db = new UsageDbContext(new DbContextOptionsBuilder<UsageDbContext>().UseInMemoryDatabase($"ucf_{Guid.NewGuid():N}").Options);
        var publisher = new RecordingPublisher();
        return (db, new UsageService(db, publisher, new FixedTime(Now)), publisher.Published);
    }

    /// <summary>No allowance, no overage, prepaid auto-load of 100 units at 20 each below 10 units. A first unit of usage
    /// triggers the first top-up (credit 100, its charge still open); what that published is cleared.</summary>
    private static async Task ProvisionAutoLoadAsync(UsageService usage, List<object> published)
    {
        await usage.ProvisionAsync(TenantId, Email, MeterType.Request, 0, overageAllowed: false, 20, "USD", null, default);
        await usage.ConfigureAutoLoadAsync(TenantId, Email, MeterType.Request, true, 10, 100, default);
        await usage.RecordAsync(TenantId, Email, MeterType.Request, 1, "seed", default);
        published.Clear();
    }

    private static Task<UsageBalance> BalanceAsync(UsageDbContext db) => db.UsageBalances.AsNoTracking().SingleAsync();

    private static UsageChargeFailed Failed(string reference, UsageChargeKind kind, long quantity, bool final) =>
        new(TenantId, null, Email, kind, MeterType.Request, quantity, reference, 2_000, "USD", ChargeFailureReason.Failed, final ? 3 : 0, Now,
            final ? null : Now.AddDays(1), Now.AddDays(7), final);

    private static UsageChargeSettled Settled(string reference, UsageChargeKind kind, long quantity) =>
        new(TenantId, null, Email, kind, MeterType.Request, quantity, 20, reference, "USD", 2_000, 0, 2_000, false, TaxScheme.Domestic,
            [new SettledChargeLine("usage", quantity, 2_000, TaxCategory.Standard, 0, 2_000, 0)], Now, 1);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingPublisher : IPublishEndpoint
    {
        public List<object> Published { get; } = [];

        public Task Publish<T>(T message, CancellationToken cancellationToken = default) where T : class { Published.Add(message); return Task.CompletedTask; }
        public Task Publish<T>(T message, IPipe<PublishContext<T>> publishPipe, CancellationToken cancellationToken = default) where T : class { Published.Add(message); return Task.CompletedTask; }
        public Task Publish<T>(T message, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default) where T : class { Published.Add(message); return Task.CompletedTask; }
        public Task Publish(object message, CancellationToken cancellationToken = default) { Published.Add(message); return Task.CompletedTask; }
        public Task Publish(object message, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default) { Published.Add(message); return Task.CompletedTask; }
        public Task Publish(object message, Type messageType, CancellationToken cancellationToken = default) { Published.Add(message); return Task.CompletedTask; }
        public Task Publish(object message, Type messageType, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default) { Published.Add(message); return Task.CompletedTask; }
        public Task Publish<T>(object values, CancellationToken cancellationToken = default) where T : class => throw new NotSupportedException();
        public Task Publish<T>(object values, IPipe<PublishContext<T>> publishPipe, CancellationToken cancellationToken = default) where T : class => throw new NotSupportedException();
        public Task Publish<T>(object values, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default) where T : class => throw new NotSupportedException();
        public ConnectHandle ConnectPublishObserver(IPublishObserver observer) => throw new NotSupportedException();
    }
}
