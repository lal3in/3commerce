using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.BuildingBlocks.Contracts.Tax.Facts;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;
using ThreeCommerce.Payments.Infrastructure;
using ThreeCommerce.Payments.Infrastructure.Providers;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// ADR-0063 P4b (<c>tax_p4b_settled_charge_events</c>): Payments announces every SETTLED renewal and usage charge —
/// <see cref="SubscriptionRenewalSettled"/> / <see cref="UsageChargeSettled"/> — with P1-shaped tax facts that equal the
/// ledger entry it posted (base + tax = gross, tax = the <c>tax.store-{id}</c> credit, the rate the split was pinned at),
/// exactly once per settled charge (redelivery- and webhook-replay-safe), and never for an attempt that did not settle.
/// </summary>
public class SettledChargeEventTests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RenewAt = Now.AddMonths(1).AddHours(13);

    [Theory]
    // price, bps, inclusive, exempt, rate known → category, rate, base, tax, gross, inclusive flag, scheme
    [InlineData(1000, 825, false, false, true, TaxCategory.Standard, 825, 1000, 83, 1083, false, TaxScheme.Domestic)]   // US: added
    [InlineData(2200, 1000, true, false, true, TaxCategory.Standard, 1000, 2000, 200, 2200, true, TaxScheme.Domestic)]  // AU: contained
    [InlineData(1000, 825, false, true, true, TaxCategory.Exempt, 0, 1000, 0, 1000, false, TaxScheme.Domestic)]          // ship-rule exempt
    [InlineData(1000, 0, true, false, true, TaxCategory.Standard, 0, 1000, 0, 1000, true, TaxScheme.Domestic)]           // 0 % store
    [InlineData(1000, 0, false, false, false, TaxCategory.NotRecorded, 0, 1000, 0, 1000, false, TaxScheme.NotRecorded)]  // rate unknown
    public void The_facts_are_the_split_that_was_charged_and_posted(
        long price, int bps, bool inclusive, bool exempt, bool rateKnown,
        TaxCategory category, int rate, long taxableBase, long tax, long gross, bool taxInclusive, TaxScheme scheme)
    {
        var split = RecurringChargeTax.For(price, rateKnown ? bps : null, inclusive, exempt);
        var facts = SettledChargeTax.For(split, "line", 1, exempt, inclusive, fallbackRateBps: null);

        Assert.Equal((category, rate, taxableBase, tax), (facts.Line.TaxCategory, facts.Line.TaxRateBps, facts.Line.TaxableBaseMinor, facts.Line.TaxMinor));
        Assert.Equal(exempt ? TaxExemptionReason.DestinationShipRule : TaxExemptionReason.None, facts.Line.ExemptionReason);
        Assert.Equal((gross, tax, gross - tax, taxInclusive, scheme), (facts.GrossMinor, facts.TaxMinor, facts.NetMinor, facts.TaxInclusive, facts.Scheme));
        Assert.Equal(split.ChargeMinor, facts.Line.TaxableBaseMinor + facts.Line.TaxMinor); // base + tax = what was paid
        Assert.Equal(price, facts.Line.AmountMinor);                                       // the shelf amount
    }

    [Fact]
    public void A_split_pinned_before_rates_were_recorded_reports_the_current_rate()
    {
        var legacy = new RecurringChargeTaxResult(1000, 83, RecurringChargeTaxBasis.Exclusive); // no RateBps (pinned pre-P4b)

        Assert.Equal(825, SettledChargeTax.For(legacy, "line", 1, false, false, fallbackRateBps: 825).Line.TaxRateBps);
        Assert.Equal(825, RecurringChargeTax.For(1000, 825, taxInclusive: false).RateBps);   // new pins carry it
    }

    [Fact]
    public async Task A_settled_renewal_is_announced_once_with_the_facts_of_its_ledger_entry()
    {
        var name = $"sce_{Guid.NewGuid():N}";
        await using var db = NewDb(name);
        var store = AddStore(db, 825, inclusive: false);
        var subscription = await StartAsync(db, store, 2000, quantity: 2);
        var publisher = new RecordingPublisher();
        var provider = new RecordingProvider();

        await Renewals(db, provider, publisher, RenewAt).RenewAsync(TenantId, subscription.Id, default);

        var settled = Assert.IsType<SubscriptionRenewalSettled>(Assert.Single(publisher.Published));
        var entry = await db.JournalEntries.AsNoTracking().Include(e => e.Lines).SingleAsync();
        Assert.Equal($"renew-{subscription.Id}-2", settled.ChargeReference);
        Assert.Equal(entry.Reference, settled.ChargeReference);
        Assert.Equal((2, subscription.CurrentPeriodEnd, subscription.CurrentPeriodEnd.AddMonths(1)), (settled.Sequence, settled.PeriodStart, settled.PeriodEnd));
        Assert.Equal((TenantId, (Guid?)store, subscription.Id, subscription.OrderId, "renewal@example.com", "USD"),
            (settled.TenantId, settled.StorefrontId, settled.SubscriptionId, settled.OrderId, settled.CustomerEmail, settled.Currency));
        Assert.Equal(Debit(entry, Accounts.CashStoreFor(store, "stripe")), settled.GrossMinor);   // 2165
        Assert.Equal(Credit(entry, $"tax.store-{store:N}"), settled.TaxMinor);                   // 165
        Assert.Equal(Credit(entry, $"revenue.store-{store:N}"), settled.NetMinor);               // 2000
        var line = Assert.Single(settled.Lines);
        Assert.Equal((2L, 2000L, TaxCategory.Standard, 825, 2000L, 165L), (line.Quantity, line.AmountMinor, line.TaxCategory, line.TaxRateBps, line.TaxableBaseMinor, line.TaxMinor));
        Assert.Equal((false, TaxScheme.Domestic), (settled.TaxInclusive, settled.TaxScheme));
        Assert.Equal(settled.TaxMinor, settled.Lines.Sum(l => l.TaxMinor));
    }

    [Theory]
    [InlineData(PaymentOutcome.Failed)]
    [InlineData(PaymentOutcome.RequiresAction)]
    [InlineData(PaymentOutcome.Cancelled)]
    public async Task An_unsettled_renewal_announces_nothing_settled(PaymentOutcome outcome)
    {
        var name = $"sce_{Guid.NewGuid():N}";
        await using var db = NewDb(name);
        var store = AddStore(db, 825, inclusive: false);
        var subscription = await StartAsync(db, store, 1000);
        var publisher = new RecordingPublisher();

        await Renewals(db, new RecordingProvider { Outcome = outcome }, publisher, RenewAt).RenewAsync(TenantId, subscription.Id, default);

        Assert.DoesNotContain(publisher.Published, m => m is SubscriptionRenewalSettled);
        Assert.Empty(await db.JournalEntries.ToListAsync());
    }

    [Fact]
    public async Task A_renewal_settled_later_by_the_providers_success_event_is_announced_exactly_once()
    {
        // The #315 3-D Secure path: the renewal asks for authentication (no settled event), the customer completes it and
        // the provider's success event arrives — twice. One settled event, carrying the rate the period was pinned at even
        // though the store's rate changed in between.
        var name = $"sce_{Guid.NewGuid():N}";
        await using var db = NewDb(name);
        var store = AddStore(db, 825, inclusive: false);
        var subscription = await StartAsync(db, store, 1000);
        var provider = new RecordingProvider { Outcome = PaymentOutcome.RequiresAction };
        var first = new RecordingPublisher();
        await Renewals(db, provider, first, RenewAt).RenewAsync(TenantId, subscription.Id, default);
        Assert.DoesNotContain(first.Published, m => m is SubscriptionRenewalSettled);
        var intent = (await NewDb(name).SubscriptionChargeAttempts.SingleAsync()).PaymentIntentId!;
        await SetRateAsync(db, store, 1000);

        var published = new List<object>();
        for (var delivery = 0; delivery < 2; delivery++)
        {
            await using var webhookDb = NewDb(name);
            var publisher = new RecordingPublisher();
            Assert.True(await Renewals(webhookDb, provider, publisher, RenewAt.AddHours(1)).SettleAttemptAsync(intent, default));
            await webhookDb.SaveChangesAsync();
            published.AddRange(publisher.Published);
        }

        var settled = Assert.IsType<SubscriptionRenewalSettled>(Assert.Single(published));
        Assert.Equal((2, 1083L, 83L, 825), (settled.Sequence, settled.GrossMinor, settled.TaxMinor, Assert.Single(settled.Lines).TaxRateBps));
    }

    [Fact]
    public async Task A_settled_usage_charge_is_announced_once_with_the_facts_of_its_ledger_entry()
    {
        await using var db = NewDb($"sce_{Guid.NewGuid():N}");
        var store = AddStore(db, 1000, inclusive: true);
        var publisher = new RecordingPublisher();
        var provider = new RecordingProvider();
        const string reference = "autoload-sce-1";

        for (var delivery = 0; delivery < 2; delivery++) // a redelivered request never charges, posts or announces twice
        {
            await UsageBilling.For(db, provider, new FixedTime(Now), publisher)
                .ChargeAsync(UsageBilling.Request(store, 1100, reference, UsageChargeKind.AutoLoad, quantity: 50, currency: "AUD"), default);
        }

        var settled = Assert.IsType<UsageChargeSettled>(Assert.Single(publisher.Published));
        var entry = await db.JournalEntries.AsNoTracking().Include(e => e.Lines).SingleAsync();
        Assert.Equal((reference, UsageChargeKind.AutoLoad, MeterType.Token, 50L, 22L), (settled.Reference, settled.Kind, settled.Meter, settled.Quantity, settled.UnitPriceMinor));
        Assert.Equal(entry.Reference, settled.Reference);
        Assert.Equal(Debit(entry, Accounts.CashStoreFor(store, "stripe")), settled.GrossMinor); // 1100: contained
        Assert.Equal(Credit(entry, $"tax.store-{store:N}"), settled.TaxMinor);                  // 100
        var line = Assert.Single(settled.Lines);
        Assert.Equal((TaxCategory.Standard, 1000, 1000L, 100L), (line.TaxCategory, line.TaxRateBps, line.TaxableBaseMinor, line.TaxMinor));
        Assert.Equal((true, TaxScheme.Domestic, "AUD", 0), (settled.TaxInclusive, settled.TaxScheme, settled.Currency, settled.Attempt));
    }

    [Theory]
    [InlineData(PaymentOutcome.Failed)]
    [InlineData(PaymentOutcome.RequiresAction)]
    [InlineData(PaymentOutcome.Cancelled)]
    public async Task An_unsettled_usage_charge_announces_a_failure_and_nothing_settled(PaymentOutcome outcome)
    {
        await using var db = NewDb($"sce_{Guid.NewGuid():N}");
        var store = AddStore(db, 825, inclusive: false);
        var publisher = new RecordingPublisher();

        await UsageBilling.For(db, new RecordingProvider { Outcome = outcome }, new FixedTime(Now), publisher)
            .ChargeAsync(UsageBilling.Request(store, 1000, "overage-sce-unsettled"), default);

        Assert.DoesNotContain(publisher.Published, m => m is UsageChargeSettled);
        var failed = Assert.IsType<UsageChargeFailed>(Assert.Single(publisher.Published));
        Assert.Equal((1083L, 0, false), (failed.AmountMinor, failed.Attempt, failed.Final));
        Assert.Empty(await db.JournalEntries.ToListAsync());
    }

    // ---- Helpers --------------------------------------------------------------------------------------

    private static PaymentsDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseInMemoryDatabase(name).Options);

    private static SubscriptionService Renewals(PaymentsDbContext db, RecordingProvider provider, RecordingPublisher publisher, DateTimeOffset at) =>
        new(db, new SingleProviderRegistry(provider),
            new PaymentModeResolver(PaymentTestSupport.Config(("Payments:Mode", "Sandbox")), PaymentTestSupport.Env(Environments.Development)),
            new FixedTime(at), new ListLogger<SubscriptionService>(), publisher);

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

    private static async Task SetRateAsync(PaymentsDbContext db, Guid store, int bps)
    {
        var row = await db.StorefrontLedgerAccounts.SingleAsync(x => x.StorefrontId == store);
        row.TaxRateBasisPoints = bps;
        await db.SaveChangesAsync();
    }

    private static async Task<Subscription> StartAsync(PaymentsDbContext db, Guid store, long price, int quantity = 1)
    {
        var subscription = Subscription.Start(
            TenantId, Guid.CreateVersion7(), "renewal@example.com", Guid.CreateVersion7(), null, BillingPeriod.Monthly,
            price, "USD", Now, "cus_1", "pm_1", store, taxInclusive: false, quantity: quantity);
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return subscription;
    }

    private static long Credit(JournalEntry e, string account) => e.Lines.Where(l => l.AccountCode == account).Sum(l => l.CreditMinor);

    private static long Debit(JournalEntry e, string account) => e.Lines.Where(l => l.AccountCode == account).Sum(l => l.DebitMinor);
}
