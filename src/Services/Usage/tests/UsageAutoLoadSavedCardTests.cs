using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Usage.Domain;
using ThreeCommerce.Usage.Infrastructure;

namespace ThreeCommerce.Usage.Tests;

/// <summary>
/// jobmgr_3_settlement (owner decision 2026-11-06): an auto top-up charges the customer's DEFAULT saved card; with no card
/// Payments skips it (<see cref="UsageAutoLoadSkipped"/>). Usage then takes the optimistic credit back (once), pauses
/// auto-load — no further top-up from usage or the daily sweep, which would only be skipped and email the customer again —
/// and resumes when Payments reports a card (<see cref="CustomerPaymentMethodReady"/>), topping up at once when the balance
/// is at its threshold. Nothing is owed for a skipped top-up, so it holds nothing.
/// </summary>
public class UsageAutoLoadSavedCardTests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    // A sub-microsecond tick (engineering-gotchas: pin the clock so StoredTime is exercised on a Mac too).
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero).AddTicks(1_755_249);
    private const string Email = "metered@example.com";

    [Fact]
    public async Task A_skipped_top_up_takes_back_its_credit_once_and_pauses_auto_load_without_a_hold()
    {
        var (db, usage, published) = Setup();
        await ProvisionAutoLoadAsync(usage, published);

        await usage.RecordAsync(TenantId, Email, MeterType.Request, 95, "u1", default); // credit 100 → 5 ≤ 10: top up 100
        var charge = Assert.IsType<UsageAutoLoadCharge>(Assert.Single(published));
        Assert.Equal(105, (await BalanceAsync(db)).PrepaidRemainingQuantity);

        var skipped = Skipped(charge.Reference, Now);
        await usage.ApplySkippedAsync(skipped, default);
        await usage.ApplySkippedAsync(skipped, default);                         // redelivered

        var balance = await BalanceAsync(db);
        Assert.Equal(5, balance.PrepaidRemainingQuantity);                      // taken back exactly once
        Assert.True(balance.AutoLoadAwaitingPaymentMethod);
        Assert.Equal(UsagePaymentHold.None, balance.PaymentHold);               // nothing is owed
        Assert.Equal(UsageChargeState.Skipped, (await db.UsageCharges.SingleAsync(c => c.Reference == charge.Reference)).State);

        await usage.RecordAsync(TenantId, Email, MeterType.Request, 5, "u2", default); // the credit is used up: still no top-up
        Assert.Single(published);
        Assert.Equal(0, await usage.SweepAutoLoadsAsync(default));              // nor from the daily safety net
        Assert.Single(published);
        // Beyond the credit, with no card and no overage allowed, billable usage is refused (it could never be paid).
        await Assert.ThrowsAsync<UsageRuleException>(() => usage.RecordAsync(TenantId, Email, MeterType.Request, 1, "u3", default));
    }

    [Fact]
    public async Task A_card_reported_after_the_skip_resumes_auto_load_and_tops_up_at_once()
    {
        var (db, usage, published) = Setup();
        await ProvisionAutoLoadAsync(usage, published);
        await usage.RecordAsync(TenantId, Email, MeterType.Request, 95, "u1", default);
        var first = Assert.IsType<UsageAutoLoadCharge>(Assert.Single(published));
        await usage.ApplySkippedAsync(Skipped(first.Reference, Now), default);

        await usage.ApplyPaymentMethodReadyAsync(new CustomerPaymentMethodReady(TenantId, "Metered@Example.com", Now.AddMinutes(5)), default);

        var balance = await BalanceAsync(db);
        Assert.False(balance.AutoLoadAwaitingPaymentMethod);
        var next = Assert.IsType<UsageAutoLoadCharge>(published[1]);            // the balance sat at its threshold: top up now
        Assert.NotEqual(first.Reference, next.Reference);
        Assert.Equal(105, balance.PrepaidRemainingQuantity);

        await usage.ApplyPaymentMethodReadyAsync(new CustomerPaymentMethodReady(TenantId, Email, Now.AddMinutes(5)), default); // redelivered
        Assert.Equal(2, published.Count);                                       // nothing more
    }

    [Fact]
    public async Task A_card_reported_before_the_skip_neither_resumes_a_later_pause_nor_lets_a_crossed_skip_pause()
    {
        // Stale: the card was reported, then removed, then a top-up was skipped. A redelivered old report must not resume.
        var (db, usage, published) = Setup();
        await ProvisionAutoLoadAsync(usage, published);
        await usage.ApplyPaymentMethodReadyAsync(new CustomerPaymentMethodReady(TenantId, Email, Now.AddMinutes(-30)), default);
        await usage.RecordAsync(TenantId, Email, MeterType.Request, 95, "u1", default);
        var charge = Assert.IsType<UsageAutoLoadCharge>(Assert.Single(published));
        await usage.ApplySkippedAsync(Skipped(charge.Reference, Now), default);
        await usage.ApplyPaymentMethodReadyAsync(new CustomerPaymentMethodReady(TenantId, Email, Now.AddMinutes(-30)), default);
        Assert.True((await BalanceAsync(db)).AutoLoadAwaitingPaymentMethod);

        // Crossed: the card was saved after the skip, but its report arrived first. The skip must not pause.
        var (db2, usage2, published2) = Setup();
        await ProvisionAutoLoadAsync(usage2, published2);
        await usage2.RecordAsync(TenantId, Email, MeterType.Request, 95, "u1", default);
        var crossed = Assert.IsType<UsageAutoLoadCharge>(Assert.Single(published2));
        await usage2.ApplyPaymentMethodReadyAsync(new CustomerPaymentMethodReady(TenantId, Email, Now.AddMinutes(1)), default);
        await usage2.ApplySkippedAsync(Skipped(crossed.Reference, Now), default);
        var balance = await BalanceAsync(db2);
        Assert.False(balance.AutoLoadAwaitingPaymentMethod);
        Assert.Equal(5, balance.PrepaidRemainingQuantity);                      // the skipped credit is still taken back
    }

    [Fact]
    public async Task Configuring_auto_load_again_is_a_retry_and_a_skip_never_touches_a_settled_charge()
    {
        var (db, usage, published) = Setup();
        await ProvisionAutoLoadAsync(usage, published);
        await usage.RecordAsync(TenantId, Email, MeterType.Request, 95, "u1", default);
        var charge = Assert.IsType<UsageAutoLoadCharge>(Assert.Single(published));
        await usage.ApplySkippedAsync(Skipped(charge.Reference, Now), default);

        await usage.ConfigureAutoLoadAsync(TenantId, Email, MeterType.Request, true, 10, 100, default);
        Assert.False((await BalanceAsync(db)).AutoLoadAwaitingPaymentMethod);
        await usage.SweepAutoLoadsAsync(default);
        Assert.Equal(2, published.Count);                                       // the sweep tops it up again

        // A skip for a charge that is not open (a stale message) changes nothing.
        var row = await db.UsageCharges.SingleAsync(c => c.Reference == charge.Reference);
        Assert.False(row.Skip(Now));
    }

    [Fact]
    public void Pause_times_are_kept_at_the_precision_Postgres_stores()
    {
        var balance = UsageBalance.Create(TenantId, Email, MeterType.Request, Now);
        balance.PauseAutoLoadForPaymentMethod(Now, Now);
        Assert.Equal(Now.AddTicks(-9), balance.AutoLoadAwaitingPaymentMethodSince);
        Assert.False(balance.PaymentMethodReady(Now, Now));                     // the same instant (after truncation) is not "after"
        Assert.True(balance.PaymentMethodReady(Now.AddTicks(10), Now));
    }

    // ---- Helpers --------------------------------------------------------------------------------------

    private static (UsageDbContext Db, UsageService Usage, List<object> Published) Setup()
    {
        var db = new UsageDbContext(new DbContextOptionsBuilder<UsageDbContext>().UseInMemoryDatabase($"uasc_{Guid.NewGuid():N}").Options);
        var publisher = new RecordingPublisher();
        return (db, new UsageService(db, publisher, new FixedTime(Now)), publisher.Published);
    }

    /// <summary>No allowance, no overage, prepaid auto-load of 100 units at 20 each below 10 units; the seeding top-up is
    /// cleared from what was published.</summary>
    private static async Task ProvisionAutoLoadAsync(UsageService usage, List<object> published)
    {
        await usage.ProvisionAsync(TenantId, Email, MeterType.Request, 0, overageAllowed: false, 20, "USD", null, default);
        await usage.ConfigureAutoLoadAsync(TenantId, Email, MeterType.Request, true, 10, 100, default);
        await usage.RecordAsync(TenantId, Email, MeterType.Request, 1, "seed", default);
        published.Clear();
    }

    private static Task<UsageBalance> BalanceAsync(UsageDbContext db) => db.UsageBalances.AsNoTracking().SingleAsync();

    private static UsageAutoLoadSkipped Skipped(string reference, DateTimeOffset at) =>
        new(TenantId, null, Email, MeterType.Request, 100, reference, 2_000, "USD", UsageAutoLoadSkipReason.NoSavedCard, at);

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
