using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;
using ThreeCommerce.Payments.Infrastructure;
using ThreeCommerce.Payments.Infrastructure.Consumers;
using ThreeCommerce.Payments.Infrastructure.Providers;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// renewal_usage_refund_path (owner decision 2026-10-27, ADR-0041/0057 amendments 2026-10-28): every settled renewal and
/// usage charge is a <see cref="Payment"/> of its own — kind, the journal reference that booked it, its intent id, its
/// amount and the tax THAT entry booked — so the single refund path (<see cref="ExecuteRefundConsumer"/>, by charge
/// reference), partial refunds and the dispute webhooks (by intent) reach it. A reversal takes the charge's own tax, so a
/// full refund nets every account the charge touched — the tax account included — back to zero; the order's own payment
/// is never touched, and Ordering is told it was a charge, not the order.
/// </summary>
public class RecurringChargePaymentTests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_settled_renewal_records_its_own_payment_carrying_the_tax_its_entry_booked()
    {
        var name = Db();
        Guid store;
        Subscription subscription;
        await using (var db = NewDb(name))
        {
            store = AddStore(db, 825, inclusive: false);
            subscription = await StartAsync(db, store, 1000);
        }

        var rail = new RecordingProvider();
        await using (var db = NewDb(name))
        {
            await Service(db, rail).RenewAsync(TenantId, subscription.Id, default);
        }

        await using var check = NewDb(name);
        var payment = await check.Payments.SingleAsync();
        var reference = $"renew-{subscription.Id}-2";
        var entry = await check.JournalEntries.Include(e => e.Lines).SingleAsync(e => e.Reference == reference);
        Assert.Equal(PaymentKind.Renewal, payment.Kind);
        Assert.Equal(reference, payment.ChargeReference);
        Assert.Equal(subscription.OrderId, payment.OrderId);
        Assert.Equal(subscription.Id, payment.SubscriptionId);
        Assert.Equal("pi_rec_1", payment.PaymentIntentId);
        Assert.Equal(store, payment.StorefrontId);
        Assert.Equal(1_083, payment.AmountMinor);                                  // what was charged (price + 8.25%)
        Assert.Equal(83, payment.TaxMinor);
        Assert.Equal(Ledger.TaxOf(entry, $"tax.store-{store:N}"), payment.TaxMinor); // read from that charge's own entry
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal("stripe", payment.Provider);
        Assert.Equal($"renew-{subscription.Id}-2:chargeback", payment.ChargebackReference);
    }

    [Theory]
    [InlineData(PaymentOutcome.Failed)]
    [InlineData(PaymentOutcome.RequiresAction)]
    public async Task An_unsettled_renewal_records_no_payment(PaymentOutcome outcome)
    {
        var name = Db();
        Subscription subscription;
        await using (var db = NewDb(name))
        {
            subscription = await StartAsync(db, AddStore(db, 825, inclusive: false), 1000);
        }

        await using (var db = NewDb(name))
        {
            await Service(db, new RecordingProvider { Outcome = outcome }).RenewAsync(TenantId, subscription.Id, default);
        }

        await using var check = NewDb(name);
        Assert.Empty(await check.Payments.ToListAsync());
    }

    [Fact]
    public async Task A_3DS_renewal_settled_by_the_providers_event_records_its_payment_under_that_intent()
    {
        var name = Db();
        Subscription subscription;
        await using (var db = NewDb(name))
        {
            subscription = await StartAsync(db, AddStore(db, 1000, inclusive: true), 2200);
        }

        var rail = new RecordingProvider { Outcome = PaymentOutcome.RequiresAction };
        await using (var db = NewDb(name))
        {
            await Service(db, rail).RenewAsync(TenantId, subscription.Id, default);
        }

        await using (var db = NewDb(name))
        {
            await Processor(db, rail, new RecordingPublisher()).ProcessAsync(Event("evt_3ds", PaymentWebhookKind.PaymentSucceeded, "pi_rec_1"), default);
        }

        await using var check = NewDb(name);
        var payment = await check.Payments.SingleAsync();
        Assert.Equal(("pi_rec_1", 2_200L, 200L), (payment.PaymentIntentId, payment.AmountMinor, payment.TaxMinor)); // inclusive GST
        Assert.Equal($"renew-{subscription.Id}-2", payment.ChargeReference);
    }

    [Fact]
    public async Task A_settled_usage_charge_records_its_own_payment_and_an_unsettled_one_none()
    {
        var name = Db();
        Guid store;
        await using (var db = NewDb(name))
        {
            store = AddStore(db, 1000, inclusive: true);
        }

        var rail = new RecordingProvider();
        await using (var db = NewDb(name))
        {
            await Charge(db, rail, store, 2_200, "overage-b1-3");
        }

        rail.Outcome = PaymentOutcome.Failed;
        await using (var db = NewDb(name))
        {
            await Charge(db, rail, store, 2_200, "overage-b1-4");
        }

        await using var check = NewDb(name);
        var payment = await check.Payments.SingleAsync();
        Assert.Equal(PaymentKind.UsageCharge, payment.Kind);
        Assert.Equal("overage-b1-3", payment.ChargeReference);
        Assert.Equal(Guid.Empty, payment.OrderId);                                 // a usage charge has no order
        Assert.Null(payment.SubscriptionId);
        Assert.Equal(("pi_rec_1", 2_200L, 200L), (payment.PaymentIntentId, payment.AmountMinor, payment.TaxMinor));
        Assert.Equal(store, payment.StorefrontId);
    }

    [Fact]
    public async Task Refunding_a_renewal_by_its_charge_reference_reverses_its_net_and_tax_to_zero_and_leaves_the_order_alone()
    {
        var (name, subscription, store, rail) = await RenewedAsync(825, inclusive: false, 1000);  // 1083 = 1000 + 83
        var reference = $"renew-{subscription.Id}-2";
        await AddOrderPaymentAsync(name, subscription.OrderId, "pi_order", 1_083);

        var (completed, failed) = await RefundAsync(name, rail, new RefundRequested(Guid.CreateVersion7(), subscription.OrderId, 1_083, "renewal refund", "admin", reference));

        Assert.Null(failed);
        Assert.NotNull(completed);
        Assert.Equal((1_083L, true, reference, subscription.OrderId), (completed!.AmountMinor, completed.FullyRefunded, completed.ChargeReference, completed.OrderId));
        Assert.Equal(("pi_rec_1", 1_083L), (rail.Refunds.Single().Intent, rail.Refunds.Single().AmountMinor)); // the renewal's intent

        await using var check = NewDb(name);
        var renewal = await check.Payments.SingleAsync(p => p.ChargeReference == reference);
        var order = await check.Payments.SingleAsync(p => p.Kind == PaymentKind.Order);
        Assert.Equal((PaymentStatus.Refunded, 1_083L), (renewal.Status, renewal.RefundedMinor));
        Assert.Equal((PaymentStatus.Succeeded, 0L), (order.Status, order.RefundedMinor));            // untouched

        var sale = await check.JournalEntries.Include(e => e.Lines).SingleAsync(e => e.Reference == reference);
        var refund = await check.JournalEntries.Include(e => e.Lines).SingleAsync(e => e.Reference == completed.RefundId.ToString());
        foreach (var account in sale.Lines.Concat(refund.Lines).Select(l => l.AccountCode).Distinct())
        {
            Assert.Equal(0, Net(sale, account) + Net(refund, account));                                // every account, tax included
        }

        Assert.Equal(83, refund.Lines.Where(l => l.AccountCode == $"tax.store-{store:N}").Sum(l => l.DebitMinor));
        Assert.Contains(reference, refund.Description);
    }

    [Fact]
    public async Task Partial_refunds_of_a_usage_charge_split_its_own_tax_and_stop_at_what_remains()
    {
        var name = Db();
        Guid store;
        await using (var db = NewDb(name))
        {
            store = AddStore(db, 825, inclusive: false);
        }

        var rail = new RecordingProvider();
        await using (var db = NewDb(name))
        {
            await Charge(db, rail, store, 1_000, "autoload-b2-1");                                  // 1083 = 1000 + 83
        }

        var (first, _) = await RefundAsync(name, rail, new RefundRequested(Guid.CreateVersion7(), Guid.Empty, 500, "part", "admin", "autoload-b2-1"));
        var (second, _) = await RefundAsync(name, rail, new RefundRequested(Guid.CreateVersion7(), Guid.Empty, 583, "rest", "admin", "autoload-b2-1"));
        var (_, tooMuch) = await RefundAsync(name, rail, new RefundRequested(Guid.CreateVersion7(), Guid.Empty, 1, "more", "admin", "autoload-b2-1"));

        Assert.False(first!.FullyRefunded);
        Assert.True(second!.FullyRefunded);
        Assert.Equal(RefundFailureReason.AmountExceedsRemaining, tooMuch!.Reason);
        await using var check = NewDb(name);
        var tax = $"tax.store-{store:N}";
        var refunds = await check.JournalEntries.Include(e => e.Lines)
            .Where(e => e.Reference == first.RefundId.ToString() || e.Reference == second.RefundId.ToString()).ToListAsync();
        Assert.Equal(83, refunds.SelectMany(e => e.Lines).Where(l => l.AccountCode == tax).Sum(l => l.DebitMinor)); // 38 + 45
        Assert.Equal(PaymentStatus.Refunded, (await check.Payments.SingleAsync()).Status);
    }

    [Fact]
    public async Task A_refund_by_order_reaches_the_orders_own_payment_never_a_renewal_of_it()
    {
        var (name, subscription, _, rail) = await RenewedAsync(825, inclusive: false, 1000);
        await AddOrderPaymentAsync(name, subscription.OrderId, "pi_order", 2_000);

        var (completed, failed) = await RefundAsync(name, rail, new RefundRequested(Guid.CreateVersion7(), subscription.OrderId, 2_000, "order refund", "admin"));

        Assert.Null(failed);
        Assert.Equal(("pi_order", 2_000L), (rail.Refunds.Single().Intent, rail.Refunds.Single().AmountMinor));
        Assert.Null(completed!.ChargeReference);                                                  // Ordering moves the order
        await using var check = NewDb(name);
        Assert.Equal(0, (await check.Payments.SingleAsync(p => p.Kind == PaymentKind.Renewal)).RefundedMinor);
    }

    [Fact]
    public async Task A_charge_reference_is_refused_for_another_order_or_when_unknown()
    {
        var (name, subscription, _, rail) = await RenewedAsync(825, inclusive: false, 1000);
        var reference = $"renew-{subscription.Id}-2";

        var (_, otherOrder) = await RefundAsync(name, rail, new RefundRequested(Guid.CreateVersion7(), Guid.CreateVersion7(), 100, "x", "admin", reference));
        var (_, unknown) = await RefundAsync(name, rail, new RefundRequested(Guid.CreateVersion7(), Guid.Empty, 100, "x", "admin", "renew-nope-2"));

        Assert.Equal(RefundFailureReason.NoCapturedPayment, otherOrder!.Reason);
        Assert.Equal(RefundFailureReason.NoCapturedPayment, unknown!.Reason);
        Assert.Empty(rail.Refunds);
    }

    [Fact]
    public async Task A_dispute_on_a_renewal_reverses_that_charge_under_its_own_reference_and_a_won_dispute_reinstates_it()
    {
        var (name, subscription, store, rail) = await RenewedAsync(825, inclusive: false, 1000);
        var reference = $"renew-{subscription.Id}-2";
        var publisher = new RecordingPublisher();

        await using (var db = NewDb(name))
        {
            await Processor(db, rail, publisher).ProcessAsync(Event("evt_dp_1", PaymentWebhookKind.DisputeFundsWithdrawn, "pi_rec_1", fee: 1500), default);
        }

        await using (var db = NewDb(name))
        {
            var payment = await db.Payments.SingleAsync();
            Assert.Equal((PaymentStatus.Disputed, DisputeStatus.FundsWithdrawn), (payment.Status, payment.DisputeStatus));
            var chargeback = await db.JournalEntries.Include(e => e.Lines).SingleAsync(e => e.Reference == $"{reference}:chargeback");
            Assert.Equal(83, chargeback.Lines.Where(l => l.AccountCode == $"tax.store-{store:N}").Sum(l => l.DebitMinor)); // its own tax
            Assert.False(await db.JournalEntries.AnyAsync(e => e.Reference == $"{subscription.OrderId}:chargeback")); // not the order's
        }

        var disputed = Assert.Single(publisher.Published.OfType<PaymentDisputed>());
        Assert.Equal((1_083L, reference), (disputed.AmountMinor, disputed.ChargeReference));

        await using (var db = NewDb(name))
        {
            await Processor(db, rail, publisher).ProcessAsync(Event("evt_dp_2", PaymentWebhookKind.DisputeClosedWon, "pi_rec_1"), default);
        }

        await using var check = NewDb(name);
        Assert.True(await check.JournalEntries.AnyAsync(e => e.Reference == $"{reference}:chargeback-reinstate"));
        Assert.Equal(PaymentStatus.Succeeded, (await check.Payments.SingleAsync()).Status);
    }

    [Fact]
    public async Task A_late_success_webhook_for_a_settled_renewal_books_nothing_and_tells_no_order()
    {
        var (name, subscription, _, rail) = await RenewedAsync(825, inclusive: false, 1000);
        var publisher = new RecordingPublisher();

        await using (var db = NewDb(name))
        {
            await Processor(db, rail, publisher).ProcessAsync(Event("evt_late", PaymentWebhookKind.PaymentSucceeded, "pi_rec_1"), default);
        }

        await using var check = NewDb(name);
        Assert.Equal(1, await check.JournalEntries.CountAsync());                                       // the renewal's own entry only
        Assert.False(await check.JournalEntries.AnyAsync(e => e.Reference == subscription.OrderId.ToString()));
        Assert.Empty(publisher.Published);
        Assert.True(await check.WebhookInbox.AnyAsync(w => w.EventId == "evt_late"));
    }

    [Fact]
    public void A_settled_charge_payment_is_never_an_order_payment()
    {
        var entry = Ledger.Sale(Guid.Empty, 1_000, 0, 0, "USD", Now, reference: "overage-x-1");
        Assert.Throws<ArgumentException>(() => Payment.ForSettledCharge(
            PaymentKind.Order, entry, null, Guid.NewGuid(), null, "pi", 1_000, "USD", "stripe", null, null, null, Now));
    }

    // ---- Helpers --------------------------------------------------------------------------------------

    private static string Db() => $"rcp_{Guid.NewGuid():N}";

    private static PaymentsDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseInMemoryDatabase(name).Options);

    private static PaymentModeResolver Modes() =>
        new(PaymentTestSupport.Config(("Payments:Mode", "Sandbox")), PaymentTestSupport.Env(Environments.Development));

    private static SubscriptionService Service(PaymentsDbContext db, RecordingProvider rail) =>
        new(db, new SingleProviderRegistry(rail), Modes(), new FixedTime(Now.AddMonths(1)), new ListLogger<SubscriptionService>(), new RecordingPublisher());

    private static PaymentEventProcessor Processor(PaymentsDbContext db, RecordingProvider rail, RecordingPublisher publisher) =>
        new(db, publisher, new NoDedupe(), new FixedTime(Now.AddMonths(1)), new ListLogger<PaymentEventProcessor>(), Service(db, rail));

    private static PaymentWebhookEvent Event(string id, PaymentWebhookKind kind, string intent, long fee = 0) =>
        new(id, kind, intent, 0, fee, null, ProviderDisputeId: kind >= PaymentWebhookKind.DisputeCreated ? "dp_1" : null);

    private static Task Charge(PaymentsDbContext db, RecordingProvider rail, Guid store, long amount, string reference) =>
        UsageChargeBilling.ChargeAndPostAsync(
            db, new SingleProviderRegistry(rail), Modes(), new FixedTime(Now), new ListLogger<UsageOverageChargeConsumer>(),
            store, amount, "USD", reference, $"Usage {reference}", default);

    /// <summary>A subscription on a fresh store whose period-2 renewal settled (intent pi_rec_1).</summary>
    private static async Task<(string Name, Subscription Subscription, Guid Store, RecordingProvider Rail)> RenewedAsync(int bps, bool inclusive, long price)
    {
        var name = Db();
        Guid store;
        Subscription subscription;
        await using (var db = NewDb(name))
        {
            store = AddStore(db, bps, inclusive);
            subscription = await StartAsync(db, store, price);
        }

        var rail = new RecordingProvider();
        await using (var db = NewDb(name))
        {
            await Service(db, rail).RenewAsync(TenantId, subscription.Id, default);
        }

        return (name, subscription, store, rail);
    }

    private static async Task AddOrderPaymentAsync(string name, Guid orderId, string intent, long amount)
    {
        await using var db = NewDb(name);
        db.Payments.Add(new Payment
        {
            Id = Guid.CreateVersion7(),
            OrderId = orderId,
            PaymentIntentId = intent,
            AmountMinor = amount,
            Currency = "USD",
            Status = PaymentStatus.Succeeded,
            CreatedAt = Now,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Runs <paramref name="request"/> through the real <see cref="ExecuteRefundConsumer"/> on the in-memory test harness.</summary>
    private static async Task<(RefundCompleted? Completed, RefundFailed? Failed)> RefundAsync(string name, RecordingProvider rail, RefundRequested request)
    {
        await using var services = new ServiceCollection()
            .AddDbContext<PaymentsDbContext>(o => o.UseInMemoryDatabase(name))
            .AddSingleton<IPaymentProviderRegistry>(new SingleProviderRegistry(rail))
            .AddSingleton(Modes())
            .AddSingleton<TimeProvider>(new FixedTime(Now.AddMonths(2)))
            .AddLogging()
            .AddMassTransitTestHarness(x => x.AddConsumer<ExecuteRefundConsumer>())
            .BuildServiceProvider(true);
        var harness = services.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(request);
            Assert.True(await harness.Consumed.Any<RefundRequested>(x => x.Context.Message.RefundId == request.RefundId));
            var fault = harness.Consumed.Select<RefundRequested>(x => x.Context.Message.RefundId == request.RefundId).First().Exception;
            Assert.Null(fault);
            var completed = harness.Published.Select<RefundCompleted>(x => x.Context.Message.RefundId == request.RefundId)
                .FirstOrDefault()?.Context.Message;
            var failed = harness.Published.Select<RefundFailed>(x => x.Context.Message.RefundId == request.RefundId)
                .FirstOrDefault()?.Context.Message;
            return (completed, failed);
        }
        finally
        {
            await harness.Stop();
        }
    }

    private static long Net(JournalEntry entry, string account) =>
        entry.Lines.Where(l => l.AccountCode == account).Sum(l => l.DebitMinor - l.CreditMinor);

    private static Guid AddStore(PaymentsDbContext db, int? bps, bool inclusive)
    {
        var id = Guid.CreateVersion7();
        db.StorefrontLedgerAccounts.Add(new StorefrontLedgerAccounts
        {
            StorefrontId = id,
            ReceivableAccountCode = $"receivable.store-{id:N}",
            RevenueAccountCode = $"revenue.store-{id:N}",
            TaxAccountCode = $"tax.store-{id:N}",
            ShippingAccountCode = $"shipping.store-{id:N}",
            TaxRateBasisPoints = bps,
            TaxInclusive = inclusive,
            UpdatedAt = Now,
        });
        db.SaveChanges();
        return id;
    }

    private static async Task<Subscription> StartAsync(PaymentsDbContext db, Guid store, long price)
    {
        var accounts = await db.StorefrontLedgerAccounts.SingleAsync(a => a.StorefrontId == store);
        var subscription = Subscription.Start(
            TenantId, Guid.CreateVersion7(), "renewal@example.com", Guid.CreateVersion7(), null, BillingPeriod.Monthly,
            price, "USD", Now, "cus_1", "pm_1", store, taxInclusive: accounts.TaxInclusive);
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();
        return subscription;
    }
}
