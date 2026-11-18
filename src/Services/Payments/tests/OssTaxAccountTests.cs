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
using Facts = ThreeCommerce.BuildingBlocks.Contracts.Tax.Facts;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// ADR-0063 § 16, per-destination OSS tax accounts (<c>oss_per_destination_tax_accounts</c>). OSS VAT is owed to — and
/// returned per — each Member State of consumption, so the OSS part of a charge's tax is posted to one liability account
/// per destination (<c>tax.store-{id}.oss.{CC}</c>, a sub-account of the store's own tax account) while the domestic
/// part stays on the store's account; every refund and chargeback takes the tax back from the same accounts, and a full
/// reversal (in one go or in parts) nets every one of them to exactly zero. A charge with no OSS part is posted exactly as
/// before, and every entry balances.
/// </summary>
public class OssTaxAccountTests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    // ---- the split itself ------------------------------------------------------------------------------

    [Fact]
    public void The_split_normalizes_round_trips_and_only_fits_inside_the_tax()
    {
        var split = OssTaxSplit.Of([(" fr ", 150), ("IT", 50), ("FR", 50), ("AT", 0), ("", 9), ("DE", -3)]);

        Assert.Equal([("FR", 200L), ("IT", 50L)], split.Shares);
        Assert.Equal("FR:200,IT:50", split.Serialize());
        Assert.Equal(split.Shares, OssTaxSplit.Parse("FR:200, IT:50").Shares);
        Assert.Equal([("FR", 200L)], OssTaxSplit.Parse("FR:200,garbage,IT:x").Shares);
        Assert.Null(OssTaxSplit.None.Serialize());
        Assert.True(OssTaxSplit.Parse(null).IsEmpty);
        Assert.Same(split, split.Within(250));
        Assert.True(split.Within(249).IsEmpty); // never posted: the store's account takes the whole tax, as before

        Assert.Equal([("tax.s", 50L), ("tax.s.oss.FR", 200L), ("tax.s.oss.IT", 50L)], split.Lines("tax.s", 300));
        Assert.Equal([("tax.s.oss.FR", 200L), ("tax.s.oss.IT", 50L)], split.Lines("tax.s", 250)); // no zero domestic line
        Assert.Equal([("tax.s", 249L)], split.Lines("tax.s", 249));
        Assert.Equal("liability.tax_collected.oss.FR", Accounts.OssTaxFor(null, "fr"));
    }

    [Fact]
    public void Reversals_allocate_exactly_and_the_final_one_closes_every_destination()
    {
        var split = OssTaxSplit.Of([("FR", 200), ("IT", 50)]); // booked tax 300: 50 domestic

        Assert.Equal(split.Shares, split.Reversal(300, 300).Shares);
        Assert.Equal([("FR", 67L), ("IT", 16L)], split.Reversal(100, 300).Shares); // 16.67 / 66.67 / 16.67: ties go to the domestic bucket, then FR
        Assert.True(split.Reversal(0, 300).IsEmpty);
        Assert.True(OssTaxSplit.None.Reversal(100, 300).IsEmpty);

        // Three refunds of a third each: each destination never goes past what it was booked, and the last closes it exactly.
        var reversed = OssTaxSplit.None;
        foreach (var (tax, final) in new[] { (100L, false), (100L, false), (100L, true) })
        {
            var part = split.Reversal(tax, 300, reversed, final);
            Assert.True(part.TotalMinor <= tax);
            reversed = OssTaxSplit.Of(reversed.Shares.Concat(part.Shares).Select(s => ((string?)s.Country, s.TaxMinor)));
        }

        Assert.Equal(split.Shares, reversed.Shares);
    }

    [Fact]
    public void The_oss_accounts_are_part_of_the_stores_tax_and_never_a_shared_code()
    {
        var store = Guid.CreateVersion7();
        var sale = Ledger.Sale(Guid.NewGuid(), 1_300, 300, 0, "EUR", Now, PaymentMethodKind.Card, "stripe",
            $"revenue.store-{store:N}", $"tax.store-{store:N}", $"receivable.store-{store:N}", 0, null, store,
            ossTax: OssTaxSplit.Of([("FR", 200), ("IT", 50)]));

        Assert.Equal(300, Ledger.TaxOf(sale, $"tax.store-{store:N}"));
        Assert.Equal("FR:200,IT:50", Ledger.OssTaxOf(sale, $"tax.store-{store:N}").Serialize());
        Assert.Equal(sale.Lines.Sum(l => l.DebitMinor), sale.Lines.Sum(l => l.CreditMinor));
        Assert.DoesNotContain(sale.Lines, l => Accounts.IsSharedCode(l.AccountCode));
        Assert.True(Accounts.IsSharedCode("liability.tax_collected.oss.FR"));    // the unattributed fallback is shared
    }

    [Fact]
    public void A_charge_without_an_oss_part_posts_exactly_as_before()
    {
        var store = Guid.CreateVersion7();
        JournalEntry Post(OssTaxSplit? oss) => Ledger.Sale(Guid.Empty, 1_190, 190, 30, "EUR", Now, PaymentMethodKind.Card, "stripe",
            $"revenue.store-{store:N}", $"tax.store-{store:N}", $"receivable.store-{store:N}", 0, null, store, "ref", "d", oss);

        Assert.Equal(Shape(Post(null)), Shape(Post(OssTaxSplit.None)));
        Assert.Equal(Shape(Post(null)), Shape(Post(OssTaxSplit.Of([("FR", 191)])))); // a split that does not fit is never posted
        Assert.Single(Post(null).Lines, l => l.AccountCode == $"tax.store-{store:N}");
    }

    // ---- checkout sale → refunds / chargeback ------------------------------------------------------------

    [Fact]
    public async Task An_oss_sale_posts_each_destination_and_refunds_in_parts_net_every_tax_account_to_zero()
    {
        var name = Db();
        var store = await SeedStoreAsync(name);
        var orderId = Guid.CreateVersion7();
        await AddOrderPaymentAsync(name, store, orderId, "pi_oss", 1_300, 300, "FR:200,IT:50");
        var rail = new RecordingProvider();
        await SettleAsync(name, rail, "pi_oss");

        await using (var db = NewDb(name))
        {
            var sale = await db.JournalEntries.Include(e => e.Lines).SingleAsync(e => e.Reference == orderId.ToString());
            Assert.Equal((50L, 200L, 50L), (Credit(sale, Tax(store)), Credit(sale, Tax(store) + ".oss.FR"), Credit(sale, Tax(store) + ".oss.IT")));
            Assert.Equal(sale.Lines.Sum(l => l.DebitMinor), sale.Lines.Sum(l => l.CreditMinor));
        }

        foreach (var amount in new[] { 433L, 433L, 434L })
        {
            var (completed, failed) = await RefundAsync(name, rail, new RefundRequested(Guid.CreateVersion7(), orderId, amount, "r", "admin"));
            Assert.Null(failed);
            Assert.NotNull(completed);
        }

        await using var check = NewDb(name);
        var lines = await check.JournalLines.AsNoTracking().ToListAsync();
        foreach (var account in new[] { Tax(store), Tax(store) + ".oss.FR", Tax(store) + ".oss.IT" })
        {
            Assert.Equal(0, lines.Where(l => l.AccountCode == account).Sum(l => l.CreditMinor - l.DebitMinor));
        }

        var entries = await check.JournalEntries.Include(e => e.Lines).ToListAsync();
        Assert.All(entries, e => Assert.Equal(e.Lines.Sum(l => l.DebitMinor), e.Lines.Sum(l => l.CreditMinor)));
    }

    [Fact]
    public async Task A_chargeback_after_a_partial_refund_gives_back_exactly_what_each_destination_still_holds()
    {
        var name = Db();
        var store = await SeedStoreAsync(name);
        var orderId = Guid.CreateVersion7();
        await AddOrderPaymentAsync(name, store, orderId, "pi_cb", 1_300, 300, "FR:200,IT:50");
        var rail = new RecordingProvider();
        await SettleAsync(name, rail, "pi_cb");
        await RefundAsync(name, rail, new RefundRequested(Guid.CreateVersion7(), orderId, 500, "r", "admin"));

        await using (var db = NewDb(name))
        {
            await Processor(db, rail).ProcessAsync(Event("evt_cb", PaymentWebhookKind.DisputeFundsWithdrawn, "pi_cb", fee: 1_500), default);
        }

        await using var check = NewDb(name);
        var lines = await check.JournalLines.AsNoTracking().ToListAsync();
        foreach (var account in new[] { Tax(store), Tax(store) + ".oss.FR", Tax(store) + ".oss.IT" })
        {
            Assert.Equal(0, lines.Where(l => l.AccountCode == account).Sum(l => l.CreditMinor - l.DebitMinor));
        }
    }

    // ---- renewals ------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_oss_renewal_posts_its_vat_to_the_destinations_account_and_its_refund_reverses_it_there()
    {
        var name = Db();
        var store = await SeedStoreAsync(name, bps: 1_900, inclusive: true);
        Subscription oss, domestic;
        await using (var db = NewDb(name))
        {
            oss = await StartAsync(db, store, Facts.TaxScheme.Oss, "FR");
            domestic = await StartAsync(db, store, Facts.TaxScheme.Domestic, "DE");
        }

        var rail = new RecordingProvider();
        await using (var db = NewDb(name))
        {
            await Service(db, rail).RenewAsync(TenantId, oss.Id, default);
            await Service(db, rail).RenewAsync(TenantId, domestic.Id, default);
        }

        await using (var db = NewDb(name))
        {
            var ossSale = await db.JournalEntries.Include(e => e.Lines).SingleAsync(e => e.Reference == $"renew-{oss.Id}-2");
            Assert.Equal((0L, 200L), (Credit(ossSale, Tax(store)), Credit(ossSale, Tax(store) + ".oss.FR"))); // FR 20 % of 1200
            var domesticSale = await db.JournalEntries.Include(e => e.Lines).SingleAsync(e => e.Reference == $"renew-{domestic.Id}-2");
            Assert.Equal(192, Credit(domesticSale, Tax(store)));                                                  // negative control
            Assert.DoesNotContain(domesticSale.Lines, l => l.AccountCode.Contains(".oss.", StringComparison.Ordinal));
            var payment = await db.Payments.SingleAsync(p => p.ChargeReference == $"renew-{oss.Id}-2");
            Assert.Equal((200L, "FR:200"), (payment.TaxMinor, payment.OssTax));
        }

        var (completed, _) = await RefundAsync(name, rail, new RefundRequested(
            Guid.CreateVersion7(), oss.OrderId, 1_200, "r", "admin", $"renew-{oss.Id}-2"));
        Assert.NotNull(completed);
        await using var check = NewDb(name);
        var lines = await check.JournalLines.AsNoTracking().Where(l => l.AccountCode == Tax(store) + ".oss.FR").ToListAsync();
        Assert.Equal((200L, 200L), (lines.Sum(l => l.CreditMinor), lines.Sum(l => l.DebitMinor)));
    }

    // ---- Helpers --------------------------------------------------------------------------------------

    private static string Tax(Guid store) => $"tax.store-{store:N}";

    private static long Credit(JournalEntry entry, string account) =>
        entry.Lines.Where(l => l.AccountCode == account).Sum(l => l.CreditMinor - l.DebitMinor);

    private static List<(string, long, long)> Shape(JournalEntry entry) =>
        entry.Lines.Select(l => (l.AccountCode, l.DebitMinor, l.CreditMinor)).ToList();

    private static string Db() => $"osstax_{Guid.NewGuid():N}";

    private static PaymentsDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseInMemoryDatabase(name).Options);

    private static PaymentModeResolver Modes() =>
        new(PaymentTestSupport.Config(("Payments:Mode", "Sandbox")), PaymentTestSupport.Env(Environments.Development));

    private static SubscriptionService Service(PaymentsDbContext db, RecordingProvider rail) =>
        new(db, new SingleProviderRegistry(rail), Modes(), new FixedTime(Now.AddMonths(1)), new ListLogger<SubscriptionService>(), new RecordingPublisher());

    private static PaymentEventProcessor Processor(PaymentsDbContext db, RecordingProvider rail) =>
        new(db, new RecordingPublisher(), new NoDedupe(), new FixedTime(Now), new ListLogger<PaymentEventProcessor>(), Service(db, rail),
            UsageBilling.For(db, rail, new FixedTime(Now)));

    private static PaymentWebhookEvent Event(string id, PaymentWebhookKind kind, string intent, long fee = 0) =>
        new(id, kind, intent, 0, fee, null, ProviderDisputeId: kind >= PaymentWebhookKind.DisputeCreated ? "dp_1" : null);

    private static async Task SettleAsync(string name, RecordingProvider rail, string intent)
    {
        await using var db = NewDb(name);
        await Processor(db, rail).ProcessAsync(Event($"evt_{intent}", PaymentWebhookKind.PaymentSucceeded, intent), default);
    }

    private static async Task<Guid> SeedStoreAsync(string name, int bps = 2_000, bool inclusive = true)
    {
        await using var db = NewDb(name);
        var id = Guid.CreateVersion7();
        db.StorefrontLedgerAccounts.Add(new StorefrontLedgerAccounts
        {
            StorefrontId = id,
            ReceivableAccountCode = $"receivable.store-{id:N}",
            RevenueAccountCode = $"revenue.store-{id:N}",
            TaxAccountCode = Tax(id),
            ShippingAccountCode = $"shipping.store-{id:N}",
            TaxRateBasisPoints = bps,
            TaxInclusive = inclusive,
            UpdatedAt = Now,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task AddOrderPaymentAsync(string name, Guid store, Guid orderId, string intent, long amount, long tax, string ossTax)
    {
        await using var db = NewDb(name);
        db.Payments.Add(new Payment
        {
            Id = Guid.CreateVersion7(),
            OrderId = orderId,
            StorefrontId = store,
            PaymentIntentId = intent,
            AmountMinor = amount,
            TaxMinor = tax,
            OssTax = ossTax,
            Currency = "EUR",
            Status = PaymentStatus.Pending,
            CreatedAt = Now,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Subscription> StartAsync(PaymentsDbContext db, Guid store, Facts.TaxScheme scheme, string destination)
    {
        var subscription = Subscription.Start(
            TenantId, Guid.CreateVersion7(), "oss-tax@example.com", Guid.CreateVersion7(), null, BillingPeriod.Monthly,
            1_200, "EUR", Now, "cus_1", "pm_1", store, taxInclusive: true, taxScheme: scheme, taxDestinationCountry: destination);
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();
        return subscription;
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
            Assert.Null(harness.Consumed.Select<RefundRequested>(x => x.Context.Message.RefundId == request.RefundId).First().Exception);
            return (harness.Published.Select<RefundCompleted>(x => x.Context.Message.RefundId == request.RefundId).FirstOrDefault()?.Context.Message,
                harness.Published.Select<RefundFailed>(x => x.Context.Message.RefundId == request.RefundId).FirstOrDefault()?.Context.Message);
        }
        finally
        {
            await harness.Stop();
        }
    }
}
