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
/// ADR-0063 § 16, OSS renewals (owner decision 2026-11-06: an OSS-taxed subscription renews at the destination country's
/// rate). A subscription whose line was sold under OSS renews at its DESTINATION's standard rate in force on the charge's
/// LOCAL date (Payments' <see cref="EuStandardVatRates"/>), not the storefront's; its settled event reports scheme Oss,
/// that rate and the destination; the period's amount stays PINNED across retries and a rate change in between; a
/// Domestic / older (null) subscription keeps the storefront's rate; an exempt OSS line posts no tax; an unknown
/// destination falls back to the storefront's rate with an error (never a guess).
/// </summary>
public class OssRenewalTaxTests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_oss_subscription_renews_at_its_destinations_rate_not_the_storefronts()
    {
        await using var db = NewDb();
        var store = AddStore(db, 1_900, inclusive: true);                       // a German store, 19 % VAT inclusive
        var subscription = await StartAsync(db, store, 1_200, inclusive: true, TaxScheme.Oss, "fr");
        var publisher = new RecordingPublisher();
        var provider = new RecordingProvider();

        await Renewals(db, provider, publisher, Start.AddMonths(1)).RenewAsync(TenantId, subscription.Id, default);

        Assert.Equal(1_200, Assert.Single(provider.Requests).AmountMinor);       // inclusive: the shelf price is the charge
        var entry = await db.JournalEntries.AsNoTracking().Include(e => e.Lines).SingleAsync();
        Assert.Equal(200, Credit(entry, $"tax.store-{store:N}"));                // FR 20 % contained: 1200 × 2000 / 12000
        Assert.Equal(1_000, Credit(entry, $"revenue.store-{store:N}"));
        var settled = Assert.IsType<SubscriptionRenewalSettled>(Assert.Single(publisher.Published));
        var line = Assert.Single(settled.Lines);
        Assert.Equal((TaxScheme.Oss, "FR", 2_000, 200L), (settled.TaxScheme, settled.TaxDestinationCountry, line.TaxRateBps, line.TaxMinor));
    }

    [Theory]
    [InlineData(20, 30, 2_200)] // 20:30 UTC on 30 June = 23:30 in Tallinn: still June, 22 %
    [InlineData(21, 30, 2_400)] // 21:30 UTC on 30 June = 00:30 on 1 July in Tallinn: 24 % (the UTC date would say 22 %)
    public async Task The_destination_rate_is_the_one_in_force_on_the_local_date_of_the_charge(int hour, int minute, int expectedBps)
    {
        await using var db = NewDb();
        var store = AddStore(db, 1_900, inclusive: false);
        var subscription = await StartAsync(db, store, 1_000, inclusive: false, TaxScheme.Oss, "EE");
        var provider = new RecordingProvider();

        await Renewals(db, provider, new RecordingPublisher(), new DateTimeOffset(2025, 6, 30, hour, minute, 0, TimeSpan.Zero))
            .RenewAsync(TenantId, subscription.Id, default);

        Assert.Equal(1_000 + (1_000 * expectedBps / 10_000), Assert.Single(provider.Requests).AmountMinor); // exclusive: added
    }

    [Fact]
    public async Task A_retry_after_the_destinations_rate_changed_charges_the_pinned_amount()
    {
        await using var db = NewDb();
        var store = AddStore(db, 1_900, inclusive: false);
        var subscription = await StartAsync(db, store, 1_000, inclusive: false, TaxScheme.Oss, "EE");
        var provider = new RecordingProvider();
        provider.Outcomes.Enqueue(PaymentOutcome.Failed);

        await Renewals(db, provider, new RecordingPublisher(), new DateTimeOffset(2025, 6, 29, 9, 0, 0, TimeSpan.Zero)).RenewAsync(TenantId, subscription.Id, default);
        db.ChangeTracker.Clear();
        await Renewals(db, provider, new RecordingPublisher(), new DateTimeOffset(2025, 7, 2, 9, 0, 0, TimeSpan.Zero)).RenewAsync(TenantId, subscription.Id, default);

        Assert.Equal([1_220L, 1_220L], provider.Requests.Select(r => r.AmountMinor)); // 22 % pinned, not 24 %
        Assert.NotEqual(provider.Requests[0].IdempotencyKey, provider.Requests[1].IdempotencyKey);
    }

    [Theory]
    [InlineData(TaxScheme.Domestic, "FR")]
    [InlineData(null, null)] // set up before the scheme was carried
    public async Task A_domestic_or_older_subscription_keeps_the_storefronts_rate(TaxScheme? scheme, string? destination)
    {
        await using var db = NewDb();
        var store = AddStore(db, 1_900, inclusive: true);
        var subscription = await StartAsync(db, store, 1_190, inclusive: true, scheme, destination);
        var publisher = new RecordingPublisher();

        await Renewals(db, new RecordingProvider(), publisher, Start.AddMonths(1)).RenewAsync(TenantId, subscription.Id, default);

        var settled = Assert.IsType<SubscriptionRenewalSettled>(Assert.Single(publisher.Published));
        Assert.Equal((TaxScheme.Domestic, (string?)null, 1_900, 190L),
            (settled.TaxScheme, settled.TaxDestinationCountry, settled.Lines[0].TaxRateBps, settled.TaxMinor));
    }

    [Fact]
    public async Task An_exempt_oss_line_posts_no_tax_and_an_unknown_destination_falls_back_to_the_storefronts_rate()
    {
        await using var db = NewDb();
        var store = AddStore(db, 1_900, inclusive: false);
        var exempt = await StartAsync(db, store, 1_000, inclusive: false, TaxScheme.Oss, "FR", exempt: true);
        var unknown = await StartAsync(db, store, 1_000, inclusive: false, TaxScheme.Oss, "CH"); // not a Member State
        var provider = new RecordingProvider();
        var log = new ListLogger<SubscriptionService>();

        await Renewals(db, provider, new RecordingPublisher(), Start.AddMonths(1), log).RenewAsync(TenantId, exempt.Id, default);
        await Renewals(db, provider, new RecordingPublisher(), Start.AddMonths(1), log).RenewAsync(TenantId, unknown.Id, default);

        Assert.Equal([1_000L, 1_190L], provider.Requests.Select(r => r.AmountMinor));
        Assert.Contains(log.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error && e.Message.Contains("CH", StringComparison.Ordinal));
    }

    [Fact]
    public void The_scheme_reported_is_oss_only_for_a_recorded_split()
    {
        var taxed = RecurringChargeTax.For(1_200, 2_000, taxInclusive: true);
        Assert.Equal(TaxScheme.Oss, SettledChargeTax.For(taxed, "l", 1, false, true, null, oss: true).Scheme);
        Assert.Equal(TaxScheme.Domestic, SettledChargeTax.For(taxed, "l", 1, false, true, null).Scheme);
        Assert.Equal(TaxScheme.NotRecorded, SettledChargeTax.For(RecurringChargeTax.For(1_200, null, true), "l", 1, false, true, null, oss: true).Scheme);
    }

    [Fact]
    public void The_subscription_normalizes_its_destination_and_only_an_oss_scheme_has_one()
    {
        var oss = Subscription.Start(TenantId, Guid.NewGuid(), "a@b.c", Guid.NewGuid(), null, BillingPeriod.Monthly, 100, "EUR", Start,
            taxScheme: TaxScheme.Oss, taxDestinationCountry: " el ");
        var domestic = Subscription.Start(TenantId, Guid.NewGuid(), "a@b.c", Guid.NewGuid(), null, BillingPeriod.Monthly, 100, "EUR", Start,
            taxScheme: TaxScheme.Domestic, taxDestinationCountry: "FR");
        Assert.Equal(("EL", "GR"), (oss.TaxDestinationCountry, oss.OssDestination));
        Assert.Null(domestic.OssDestination);
        Assert.Equal(2_400, EuStandardVatRates.StandardRateFor("EL", new DateOnly(2026, 1, 1)));
        Assert.Null(EuStandardVatRates.StandardRateFor("DE", new DateOnly(2021, 6, 30))); // before the OSS start
    }

    // ---- Helpers --------------------------------------------------------------------------------------

    private static PaymentsDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseInMemoryDatabase($"oss_{Guid.NewGuid():N}").Options);

    private static SubscriptionService Renewals(
        PaymentsDbContext db, RecordingProvider provider, RecordingPublisher publisher, DateTimeOffset at, ListLogger<SubscriptionService>? log = null) =>
        new(db, new SingleProviderRegistry(provider),
            new PaymentModeResolver(PaymentTestSupport.Config(("Payments:Mode", "Sandbox")), PaymentTestSupport.Env(Environments.Development)),
            new FixedTime(at), log ?? new ListLogger<SubscriptionService>(), publisher);

    private static Guid AddStore(PaymentsDbContext db, int bps, bool inclusive)
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
            UpdatedAt = Start,
        });
        db.SaveChanges();
        return id;
    }

    private static async Task<Subscription> StartAsync(
        PaymentsDbContext db, Guid store, long price, bool inclusive, TaxScheme? scheme, string? destination, bool exempt = false)
    {
        var subscription = Subscription.Start(
            TenantId, Guid.CreateVersion7(), "oss@example.com", Guid.CreateVersion7(), null, BillingPeriod.Monthly,
            price, "EUR", Start, "cus_1", "pm_1", store, taxInclusive: inclusive, taxExempt: exempt, taxScheme: scheme,
            taxDestinationCountry: destination);
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return subscription;
    }

    private static long Credit(JournalEntry e, string account) => e.Lines.Where(l => l.AccountCode == account).Sum(l => l.CreditMinor);
}
