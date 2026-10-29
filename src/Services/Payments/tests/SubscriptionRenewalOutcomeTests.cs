using Microsoft.EntityFrameworkCore;
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
/// subscription_renew_on_failed_charge + subscription_renewal_price_quantity (owner decisions 2026-10-27, ADR-0057
/// amendment). Only a SETTLED renewal advances the period: a Failed / RequiresAction / Cancelled outcome goes down the
/// same dunning path as a thrown provider error (PastDue, period not advanced, no history row, no revenue, no tax), and a
/// retry is the same period — same idempotency key, same pinned amount. RequiresAction also asks the customer to
/// authenticate (<see cref="SubscriptionRenewalActionRequired"/>). A usage charge that does not settle posts nothing.
/// A subscription's price is the whole line (unit × quantity − the line's renewal discount, computed by Ordering), and
/// an exclusive store adds its tax on that total.
/// </summary>
public class SubscriptionRenewalOutcomeTests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(PaymentOutcome.Failed)]
    [InlineData(PaymentOutcome.RequiresAction)]
    [InlineData(PaymentOutcome.Cancelled)]
    public async Task An_unsettled_renewal_duns_and_neither_advances_the_period_nor_posts(PaymentOutcome outcome)
    {
        var name = $"sro_{Guid.NewGuid():N}";
        await using var db = NewDb(name);
        var store = AddStore(db, 825, inclusive: false);
        var subscription = await StartAsync(db, store, 1000);
        var (service, provider, _) = Service(db, outcome);

        var result = await service.RenewAsync(TenantId, subscription.Id, default);

        Assert.Equal(1083, Assert.Single(provider.Requests).AmountMinor);                     // the rail was asked
        Assert.Equal(SubscriptionStatus.PastDue, result!.Status);                              // dunned
        await using var fresh = NewDb(name);                                                    // what was COMMITTED
        var stored = await fresh.Subscriptions.AsNoTracking().Include(s => s.Renewals).SingleAsync(s => s.Id == subscription.Id);
        Assert.Equal(SubscriptionStatus.PastDue, stored.Status);
        Assert.Equal(subscription.CurrentPeriodStart, stored.CurrentPeriodStart);              // period NOT advanced
        Assert.Equal(subscription.CurrentPeriodEnd, stored.CurrentPeriodEnd);
        Assert.Equal(1, Assert.Single(stored.Renewals).Sequence);                              // no history row for it
        Assert.Empty(await fresh.JournalEntries.ToListAsync());                                // no revenue, no tax
    }

    [Fact]
    public async Task RequiresAction_asks_the_customer_to_authenticate_and_a_decline_says_when_it_is_retried()
    {
        await using var db = NewDb();
        var store = AddStore(db, 825, inclusive: false);
        var needsAuth = await StartAsync(db, store, 1000);
        var declined = await StartAsync(db, store, 1000);
        var (service, provider, publisher) = Service(db, PaymentOutcome.RequiresAction);

        await service.RenewAsync(TenantId, needsAuth.Id, default);
        provider.Outcome = PaymentOutcome.Failed;
        await service.RenewAsync(TenantId, declined.Id, default);

        // Dunning (2026-10-27): both outcomes tell the customer, with the next retry (day 1) and when it cancels (day 7).
        Assert.Equal(2, publisher.Published.Count);
        var asked = Assert.IsType<SubscriptionRenewalActionRequired>(publisher.Published[0]);
        var failed = Assert.IsType<SubscriptionPaymentFailed>(publisher.Published[1]);
        Assert.Equal(declined.Id, failed.SubscriptionId);
        Assert.Equal(1083, failed.AmountMinor);
        Assert.Equal(Now.AddMonths(1).AddDays(1), failed.NextRetryAt);
        Assert.Equal(Now.AddMonths(1).AddDays(7), failed.CancelsAfter);
        Assert.Equal(Now.AddMonths(1).AddDays(1), asked.NextRetryAt);
        Assert.Equal(needsAuth.Id, asked.SubscriptionId);
        Assert.Equal(TenantId, asked.TenantId);
        Assert.Equal(store, asked.StorefrontId);
        Assert.Equal("renewal@example.com", asked.CustomerEmail);
        Assert.Equal(1083, asked.AmountMinor);              // what the period's charge asked for, tax included (exclusive)
        Assert.Equal("USD", asked.Currency);
        Assert.Equal(needsAuth.CurrentPeriodEnd, asked.PeriodEnd);
        Assert.Equal($"renew-{needsAuth.Id}-{asked.PeriodEnd:O}", provider.Requests[0].IdempotencyKey);
    }

    [Theory]
    [InlineData(PaymentOutcome.Failed)]
    [InlineData(PaymentOutcome.RequiresAction)]
    public async Task A_retry_after_an_unsettled_renewal_uses_a_fresh_attempt_key_the_same_amount_and_renews_once(PaymentOutcome first)
    {
        await using var db = NewDb();
        var store = AddStore(db, 825, inclusive: false);
        var subscription = await StartAsync(db, store, 1000);
        var (service, provider, _) = Service(db, PaymentOutcome.Succeeded);
        provider.Outcomes.Enqueue(first);

        await service.RenewAsync(TenantId, subscription.Id, default);   // unsettled → PastDue
        await SetRateAsync(db, store, 1000);                            // the rate changes before the retry
        var renewed = await service.RenewAsync(TenantId, subscription.Id, default);

        Assert.Equal(2, provider.Requests.Count);
        // The same period, a FRESH key (a provider replays a reused key's answer — owner decision 2026-10-27).
        Assert.Equal($"renew-{subscription.Id}-{subscription.CurrentPeriodEnd:O}", provider.Requests[0].IdempotencyKey);
        Assert.Equal(provider.Requests[0].IdempotencyKey + "-a1", provider.Requests[1].IdempotencyKey);
        Assert.All(provider.Requests, r => Assert.Equal(1083, r.AmountMinor));                  // pinned, not re-priced
        Assert.Equal(SubscriptionStatus.Active, renewed!.Status);
        Assert.Equal(subscription.CurrentPeriodEnd, renewed.CurrentPeriodStart);                // advanced exactly once
        Assert.Equal(subscription.CurrentPeriodEnd.AddMonths(1), renewed.CurrentPeriodEnd);
        var renewals = await db.SubscriptionRenewals.AsNoTracking().Where(r => r.SubscriptionId == subscription.Id).ToListAsync();
        Assert.Equal([1, 2], renewals.Select(r => r.Sequence).Order());
        var entry = await db.JournalEntries.AsNoTracking().Include(e => e.Lines).SingleAsync();
        Assert.Equal($"renew-{subscription.Id}-2", entry.Reference);
        Assert.Equal(83, entry.Lines.Where(l => l.AccountCode == $"tax.store-{store:N}").Sum(l => l.CreditMinor));
    }

    [Fact]
    public async Task The_nightly_run_duns_an_unsettled_renewal_and_retries_it_from_day_one()
    {
        await using var db = NewDb();
        var store = AddStore(db, 0, inclusive: false);
        var first = await StartAsync(db, store, 1000);
        var second = await StartAsync(db, store, 1000);
        var provider = new RecordingProvider();
        provider.Outcomes.Enqueue(PaymentOutcome.Succeeded);
        provider.Outcomes.Enqueue(PaymentOutcome.Failed);   // whichever renews second is declined

        // 13:00 on the day the first periods end: past the schedule's default 12:00 run time.
        await Nightly(db, provider, Now.AddMonths(1).AddHours(13)).AutoRenewDueAsync(default);

        var statuses = await db.Subscriptions.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Status);
        var failed = new[] { first, second }.Single(s => provider.Requests[1].IdempotencyKey.StartsWith($"renew-{s.Id}-", StringComparison.Ordinal));
        Assert.Equal(SubscriptionStatus.PastDue, statuses[failed.Id]);
        Assert.Equal(1, statuses.Count(kv => kv.Value == SubscriptionStatus.Active));
        Assert.Single(await db.JournalEntries.ToListAsync());

        // Later the same day nothing more happens; the next day's run is the first scheduled retry: a fresh key, the same amount.
        await Nightly(db, provider, Now.AddMonths(1).AddHours(20)).AutoRenewDueAsync(default);
        Assert.Equal(2, provider.Requests.Count);
        await Nightly(db, provider, Now.AddMonths(1).AddDays(1).AddHours(13)).AutoRenewDueAsync(default);
        Assert.Equal(3, provider.Requests.Count);
        Assert.Equal(provider.Requests[1].IdempotencyKey + "-a1", provider.Requests[2].IdempotencyKey);
        Assert.Equal(provider.Requests[1].AmountMinor, provider.Requests[2].AmountMinor);
        Assert.Equal(SubscriptionStatus.Active, (await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == failed.Id)).Status);
    }

    [Theory]
    [InlineData(PaymentOutcome.Failed)]
    [InlineData(PaymentOutcome.RequiresAction)]
    [InlineData(PaymentOutcome.Cancelled)]
    public async Task An_unsettled_usage_charge_posts_nothing_logs_it_and_its_scheduled_retry_charges_the_same_amount(PaymentOutcome outcome)
    {
        await using var db = NewDb();
        var store = AddStore(db, 825, inclusive: false);
        var provider = new RecordingProvider { Outcome = outcome };
        var log = new ListLogger<UsageChargeBilling>();
        const string reference = "overage-unsettled-1";

        Task Charge() => UsageBilling.For(db, provider, new FixedTime(Now), log: log).ChargeAsync(UsageBilling.Request(store, 1000, reference), default);

        await Charge();
        Assert.Empty(await db.JournalEntries.ToListAsync());                                    // no revenue, no tax
        Assert.Contains(log.Entries, e => e.Message.Contains(reference, StringComparison.Ordinal)
            && e.Message.Contains("not settled", StringComparison.Ordinal));

        await SetRateAsync(db, store, 1000);
        provider.Outcome = PaymentOutcome.Succeeded;
        await Charge();                                                                         // a re-sent request: answered already, nothing charged
        Assert.Single(provider.Requests);
        await UsageBilling.For(db, provider, new FixedTime(Now.AddDays(1).AddHours(1))).RetryDueAsync(default); // the day-1 retry
        Assert.Equal(2, provider.Requests.Count);
        Assert.All(provider.Requests, r => Assert.Equal(1083, r.AmountMinor));                  // same pinned amount
        Assert.Equal(reference + "-a1", provider.Requests[1].IdempotencyKey);                   // a fresh key
        Assert.Equal(reference, Assert.Single(await db.JournalEntries.ToListAsync()).Reference);
    }

    [Theory]
    // a subscription's price is the WHOLE line (Ordering: unit × qty − the line's renewal discount); tax rides that total
    [InlineData(4000, 2, 825, false, 4330, 330)]   // US 8.25% on 2 × 2000
    [InlineData(4400, 2, 1000, true, 4400, 400)]   // AU GST contained in 2 × 2200
    public async Task A_multi_unit_subscription_renews_at_its_line_total_with_tax_on_that_total(
        long linePrice, int quantity, int bps, bool inclusive, long expectedCharge, long expectedTax)
    {
        await using var db = NewDb();
        var store = AddStore(db, bps, inclusive);
        var started = await NewService(db, new RecordingProvider()).StartAsync(new SubscriptionRequested(
            TenantId, Guid.CreateVersion7(), "renewal@example.com", Guid.CreateVersion7(), null, BillingPeriod.Monthly,
            linePrice, "USD", store, inclusive, false, quantity), default);
        db.ChangeTracker.Clear();
        var (service, provider, _) = Service(db, PaymentOutcome.Succeeded);

        await service.RenewAsync(TenantId, started.Id, default);

        Assert.Equal(quantity, (await db.Subscriptions.AsNoTracking().SingleAsync()).Quantity);
        Assert.Equal(expectedCharge, Assert.Single(provider.Requests).AmountMinor);
        var entry = await db.JournalEntries.AsNoTracking().Include(e => e.Lines).SingleAsync();
        Assert.Equal(expectedTax, entry.Lines.Where(l => l.AccountCode == $"tax.store-{store:N}").Sum(l => l.CreditMinor));
        Assert.Equal(expectedCharge, entry.Lines.Where(l => l.AccountCode == Accounts.CashStoreFor(store, "stripe")).Sum(l => l.DebitMinor));
    }

    [Fact]
    public async Task A_subscription_requested_before_quantity_was_carried_covers_one_unit()
    {
        await using var db = NewDb();
        var store = AddStore(db, 0, inclusive: false);

        var started = await NewService(db, new RecordingProvider()).StartAsync(new SubscriptionRequested(
            TenantId, Guid.CreateVersion7(), "renewal@example.com", Guid.CreateVersion7(), null, BillingPeriod.Monthly,
            1500, "USD", store), default);

        Assert.Equal(1, started.Quantity);
        Assert.Equal(1500, started.PriceMinor);
    }

    // ---- Helpers --------------------------------------------------------------------------------------

    private static PaymentsDbContext NewDb(string? name = null) =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseInMemoryDatabase(name ?? $"sro_{Guid.NewGuid():N}").Options);

    private static PaymentModeResolver Resolver() =>
        new(PaymentTestSupport.Config(("Payments:Mode", "Sandbox")), PaymentTestSupport.Env(Environments.Development));

    private static SubscriptionService NewService(PaymentsDbContext db, RecordingProvider provider, DateTimeOffset? at = null, RecordingPublisher? publisher = null) =>
        new(db, new SingleProviderRegistry(provider), Resolver(), new FixedTime(at ?? Now.AddMonths(1)), new ListLogger<SubscriptionService>(),
            publisher ?? new RecordingPublisher());

    private static SubscriptionService Nightly(PaymentsDbContext db, RecordingProvider provider, DateTimeOffset at) => NewService(db, provider, at);

    private static (SubscriptionService Service, RecordingProvider Provider, RecordingPublisher Publisher) Service(PaymentsDbContext db, PaymentOutcome outcome)
    {
        var provider = new RecordingProvider { Outcome = outcome };
        var publisher = new RecordingPublisher();
        return (NewService(db, provider, publisher: publisher), provider, publisher);
    }

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

    private static async Task<Subscription> StartAsync(PaymentsDbContext db, Guid store, long price)
    {
        var subscription = Subscription.Start(
            TenantId, Guid.CreateVersion7(), "renewal@example.com", Guid.CreateVersion7(), null, BillingPeriod.Monthly,
            price, "USD", Now, "cus_1", "pm_1", store, taxInclusive: false);
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return subscription;
    }
}
