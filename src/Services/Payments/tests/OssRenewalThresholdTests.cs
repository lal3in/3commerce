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
/// ADR-0063 § 16, OSS threshold crossing (<c>oss_renewal_threshold_crossing</c>). A subscription sold as a Domestic supply
/// into another Member State — the seller was at or under the EUR 10,000 threshold at checkout — is re-decided at every
/// renewal against the seller's running total (Payments' <see cref="EuDistanceSaleCopy"/> rows): once the preceding year
/// is strictly over, or the current year plus this renewal goes over, the renewal is an OSS supply at its destination's
/// standard rate. The decision is pinned with the period's amount, never downgrades an OSS subscription, and leaves a
/// home-country, non-EU, exempt, older or unconfigured case alone.
/// </summary>
public class OssRenewalThresholdTests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RenewalAt = Start.AddMonths(1); // 2026: the preceding year is 2025

    [Theory]
    [InlineData(1_000_001, 0, 1_200, TaxScheme.Oss)]          // the preceding year strictly over: FR 20 %
    [InlineData(1_000_000, 0, 1_190, TaxScheme.Domestic)]     // the preceding year AT the threshold: still the home 19 %
    [InlineData(0, 999_001, 1_200, TaxScheme.Oss)]            // this renewal (net 1,000) takes the current year over: OSS
    [InlineData(0, 999_000, 1_190, TaxScheme.Domestic)]       // ... to exactly the threshold: not over, home rate
    public async Task A_domestic_cross_border_renewal_is_re_decided_against_the_sellers_turnover(
        long previousYear, long currentYear, long expectedCharge, TaxScheme expectedScheme)
    {
        await using var db = NewDb();
        var store = AddStore(db, "DE");
        AddTurnover(db, TenantId, 2025, previousYear);
        AddTurnover(db, TenantId, 2026, currentYear);
        var subscription = await StartAsync(db, store, TaxScheme.Domestic, "FR");
        var publisher = new RecordingPublisher();
        var provider = new RecordingProvider();

        await Renewals(db, provider, publisher).RenewAsync(TenantId, subscription.Id, default);

        Assert.Equal(expectedCharge, Assert.Single(provider.Requests).AmountMinor); // exclusive: 1000 + the added VAT
        var settled = Assert.IsType<SubscriptionRenewalSettled>(Assert.Single(publisher.Published));
        Assert.Equal(
            (expectedScheme, expectedScheme == TaxScheme.Oss ? "FR" : null, expectedScheme == TaxScheme.Oss ? 2_000 : 1_900),
            (settled.TaxScheme, settled.TaxDestinationCountry, settled.Lines[0].TaxRateBps));
        // ADR-0063 § 16, per-destination OSS tax accounts: a re-decided (OSS) renewal's VAT goes to France's OSS account; a
        // Domestic one stays on the store's own (the negative control).
        var entry = await db.JournalEntries.AsNoTracking().Include(e => e.Lines).SingleAsync();
        var oss = expectedScheme == TaxScheme.Oss;
        Assert.Equal(
            (oss ? 0 : expectedCharge - 1_000, oss ? expectedCharge - 1_000 : 0),
            (TaxCredit(entry, $"tax.store-{store:N}"), TaxCredit(entry, $"tax.store-{store:N}.oss.FR")));
        var payment = await db.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(oss ? "FR:200" : null, payment.OssTax);
    }

    private static long TaxCredit(JournalEntry entry, string account) =>
        entry.Lines.Where(l => l.AccountCode == account).Sum(l => l.CreditMinor - l.DebitMinor);

    [Fact]
    public async Task Only_this_sellers_EUR_turnover_of_the_preceding_and_current_year_counts()
    {
        await using var db = NewDb();
        var store = AddStore(db, "DE");
        AddTurnover(db, Guid.NewGuid(), 2025, 5_000_000);           // another seller
        AddTurnover(db, TenantId, 2025, 5_000_000, currency: "SEK"); // not measurable in EUR
        AddTurnover(db, TenantId, 2024, 5_000_000);                 // two years back
        var subscription = await StartAsync(db, store, TaxScheme.Domestic, "FR");
        var provider = new RecordingProvider();

        await Renewals(db, provider, new RecordingPublisher()).RenewAsync(TenantId, subscription.Id, default);

        Assert.Equal(1_190, Assert.Single(provider.Requests).AmountMinor);
    }

    [Theory]
    [InlineData("DE", TaxScheme.Domestic, "DE", false)]  // a home-country supply
    [InlineData("DE", TaxScheme.Domestic, "CH", false)]  // a non-EU destination
    [InlineData("", TaxScheme.Domestic, "FR", false)]    // the store has no configured jurisdiction (projected before it)
    [InlineData("US-CA", TaxScheme.Domestic, "FR", false)] // a non-EU seller
    [InlineData("DE", null, null, false)]                // set up before the scheme was carried
    [InlineData("DE", TaxScheme.Domestic, "FR", true)]   // an exempt line stays exempt
    public async Task Other_subscriptions_are_never_re_decided_even_over_the_threshold(
        string jurisdiction, TaxScheme? scheme, string? destination, bool exempt)
    {
        await using var db = NewDb();
        var store = AddStore(db, jurisdiction);
        AddTurnover(db, TenantId, 2025, 5_000_000);
        var subscription = await StartAsync(db, store, scheme, destination, exempt: exempt);
        var publisher = new RecordingPublisher();
        var provider = new RecordingProvider();

        await Renewals(db, provider, publisher).RenewAsync(TenantId, subscription.Id, default);

        Assert.Equal(exempt ? 1_000 : 1_190, Assert.Single(provider.Requests).AmountMinor);
        Assert.NotEqual(TaxScheme.Oss, Assert.IsType<SubscriptionRenewalSettled>(Assert.Single(publisher.Published)).TaxScheme);
    }

    [Fact]
    public async Task A_currency_that_cannot_measure_the_EUR_threshold_takes_the_destination_rate_as_checkout_does()
    {
        await using var db = NewDb();
        var store = AddStore(db, "DE");
        var subscription = await StartAsync(db, store, TaxScheme.Domestic, "FR", currency: "SEK");
        var provider = new RecordingProvider();

        await Renewals(db, provider, new RecordingPublisher()).RenewAsync(TenantId, subscription.Id, default);

        Assert.Equal(1_200, Assert.Single(provider.Requests).AmountMinor);
    }

    [Fact]
    public async Task An_oss_subscription_is_never_downgraded_under_the_threshold()
    {
        await using var db = NewDb();
        var store = AddStore(db, "DE");
        var subscription = await StartAsync(db, store, TaxScheme.Oss, "FR"); // no turnover at all
        var provider = new RecordingProvider();

        await Renewals(db, provider, new RecordingPublisher()).RenewAsync(TenantId, subscription.Id, default);

        Assert.Equal(1_200, Assert.Single(provider.Requests).AmountMinor);
    }

    [Fact]
    public async Task The_decision_is_pinned_to_the_period_and_the_next_period_is_decided_afresh()
    {
        // The first attempt is made under the threshold and declined; the seller crosses before the retry. The retry of the
        // SAME period charges the pinned home-rate amount (a provider rejects a reused key with a different amount) and
        // reports Domestic; the NEXT period is a new supply, decided against the new total: OSS.
        await using var db = NewDb();
        var store = AddStore(db, "DE");
        var subscription = await StartAsync(db, store, TaxScheme.Domestic, "FR");
        var provider = new RecordingProvider();
        provider.Outcomes.Enqueue(PaymentOutcome.Failed);
        var publisher = new RecordingPublisher();

        await Renewals(db, provider, publisher).RenewAsync(TenantId, subscription.Id, default);
        AddTurnover(db, TenantId, 2026, 2_000_000);
        db.ChangeTracker.Clear();
        await Renewals(db, provider, publisher).RenewAsync(TenantId, subscription.Id, default);
        db.ChangeTracker.Clear();
        await Renewals(db, provider, publisher, RenewalAt.AddMonths(1)).RenewAsync(TenantId, subscription.Id, default);

        Assert.Equal([1_190L, 1_190L, 1_200L], provider.Requests.Select(r => r.AmountMinor));
        var settled = publisher.Published.OfType<SubscriptionRenewalSettled>().ToList();
        Assert.Equal(
            [(TaxScheme.Domestic, (string?)null), (TaxScheme.Oss, "FR")],
            settled.Select(s => (s.TaxScheme, s.TaxDestinationCountry)));

        // Each period is posted under the scheme it was pinned with: the retried period's 190 on the store's account, the
        // next period's 200 on France's OSS account (ADR-0063 § 16, per-destination OSS tax accounts).
        var entries = await db.JournalEntries.AsNoTracking().Include(e => e.Lines).ToListAsync();
        var second = Assert.Single(entries, e => e.Reference == $"renew-{subscription.Id}-2");
        var third = Assert.Single(entries, e => e.Reference == $"renew-{subscription.Id}-3");
        Assert.Equal((190L, 0L), (TaxCredit(second, $"tax.store-{store:N}"), TaxCredit(second, $"tax.store-{store:N}.oss.FR")));
        Assert.Equal((0L, 200L), (TaxCredit(third, $"tax.store-{store:N}"), TaxCredit(third, $"tax.store-{store:N}.oss.FR")));
    }

    [Fact]
    public void The_threshold_rule_matches_checkouts()
    {
        Assert.Equal(1_000_000, OssThreshold.ThresholdMinorEur);
        Assert.True(OssThreshold.Exceeded("EUR", new EuDistanceTurnover(1_000_001, 0), 0));
        Assert.False(OssThreshold.Exceeded("EUR", new EuDistanceTurnover(1_000_000, 0), 0));
        Assert.True(OssThreshold.Exceeded("EUR", new EuDistanceTurnover(0, 999_999), 2));
        Assert.False(OssThreshold.Exceeded("eur", new EuDistanceTurnover(0, 999_999), 1));
        Assert.True(OssThreshold.Exceeded("SEK", new EuDistanceTurnover(0, 0), 0));
        Assert.Equal(("DE", "DE", "GR", (string?)null, (string?)null), (OssThreshold.HomeOf("de"), OssThreshold.HomeOf("DE-BY"), OssThreshold.HomeOf("EL"),
            OssThreshold.HomeOf("XI"), OssThreshold.HomeOf("CH")));
        Assert.Equal("FR", OssThreshold.CrossBorderDestination("DE-BY", " fr "));
        Assert.Equal("XI", OssThreshold.CrossBorderDestination("IE", "XI"));
        Assert.Null(OssThreshold.CrossBorderDestination("DE", "DE"));
        Assert.Null(OssThreshold.CrossBorderDestination("DE", "GB"));
        Assert.Null(OssThreshold.CrossBorderDestination("", "FR"));
    }

    // ---- Helpers --------------------------------------------------------------------------------------

    private static PaymentsDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseInMemoryDatabase($"ossx_{Guid.NewGuid():N}").Options);

    private static SubscriptionService Renewals(PaymentsDbContext db, RecordingProvider provider, RecordingPublisher publisher, DateTimeOffset? at = null) =>
        new(db, new SingleProviderRegistry(provider),
            new PaymentModeResolver(PaymentTestSupport.Config(("Payments:Mode", "Sandbox")), PaymentTestSupport.Env(Environments.Development)),
            new FixedTime(at ?? RenewalAt), new ListLogger<SubscriptionService>(), publisher);

    /// <summary>A German-style store: 19 % EXCLUSIVE (so the charge shows the rate), in <paramref name="jurisdiction"/>.</summary>
    private static Guid AddStore(PaymentsDbContext db, string jurisdiction)
    {
        var id = Guid.CreateVersion7();
        db.StorefrontLedgerAccounts.Add(new StorefrontLedgerAccounts
        {
            StorefrontId = id,
            ReceivableAccountCode = $"receivable.store-{id:N}",
            RevenueAccountCode = $"revenue.store-{id:N}",
            TaxAccountCode = $"tax.store-{id:N}",
            ShippingAccountCode = $"shipping.store-{id:N}",
            TaxRateBasisPoints = 1_900,
            TaxInclusive = false,
            TaxJurisdiction = jurisdiction,
            UpdatedAt = Start,
        });
        db.SaveChanges();
        return id;
    }

    private static void AddTurnover(PaymentsDbContext db, Guid tenantId, int year, long netMinor, string currency = "EUR")
    {
        if (netMinor == 0)
        {
            return;
        }

        db.EuDistanceSaleCopies.Add(new EuDistanceSaleCopy
        {
            OrderId = Guid.CreateVersion7(),
            DestinationCountry = "AT",
            SourceReference = "",
            TenantId = tenantId,
            HomeCountry = "DE",
            Year = year,
            Currency = currency,
            NetMinor = netMinor,
            RecordedAt = Start,
        });
        db.SaveChanges();
    }

    private static async Task<Subscription> StartAsync(
        PaymentsDbContext db, Guid store, TaxScheme? scheme, string? destination, bool exempt = false, string currency = "EUR")
    {
        var subscription = Subscription.Start(
            TenantId, Guid.CreateVersion7(), "threshold@example.com", Guid.CreateVersion7(), null, BillingPeriod.Monthly,
            1_000, currency, Start, "cus_1", "pm_1", store, taxInclusive: false, taxExempt: exempt, taxScheme: scheme,
            taxDestinationCountry: destination);
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return subscription;
    }
}
