using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;
using ThreeCommerce.Payments.Infrastructure;
using ThreeCommerce.Payments.Infrastructure.Consumers;
using ThreeCommerce.Payments.Infrastructure.Providers;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// renewal_usage_tax_exclusive_decision (owner decision 2026-10-15, option A): on a tax-EXCLUSIVE storefront (US sales
/// tax, ADR-0038) a subscription renewal and a usage charge are charged price + the tax checkout would add — what the
/// RAIL is asked to charge, not only what the ledger books. Pinned here with a recording provider (the integration
/// suite cannot see the amount the mock rail received): the provider amount, the period's history row, and the
/// renew/usage journal entry agree, for exclusive / inclusive / exempt / 0% / unknown-rate stores, a 0-decimal
/// currency, a subscription from before the terms snapshot, and two stores sharing a currency.
/// </summary>
public class RecurringChargeAmountTests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    // price, bps, inclusive, exempt, expected charge, expected tax
    [InlineData(1000, 825, false, false, 1083, 83)]   // US 8.25%: tax ADDED (82.5 → 83, away from zero)
    [InlineData(2200, 1000, true, false, 2200, 200)]  // AU GST: contained, charge unchanged
    [InlineData(1000, 825, false, true, 1000, 0)]     // destination-exempt at purchase: nothing added
    [InlineData(1000, 0, false, false, 1000, 0)]      // a 0% exclusive store: nothing added
    public async Task A_renewal_charges_the_rail_what_checkout_would_and_books_the_same_split(
        long price, int bps, bool inclusive, bool exempt, long expectedCharge, long expectedTax)
    {
        await using var db = NewDb();
        var store = AddStore(db, bps, inclusive);
        var subscription = await StartAsync(db, store, price, "USD", inclusive, exempt);
        var (service, provider, _) = Service(db);

        await service.RenewAsync(TenantId, subscription.Id, default);

        Assert.Equal(expectedCharge, Assert.Single(provider.Requests).AmountMinor);       // what the card is charged
        Assert.Equal(expectedCharge, (await RenewalAsync(db, subscription.Id, 2)).AmountMinor); // the period history
        var entry = await EntryAsync(db, $"renew-{subscription.Id}-2");
        Assert.Equal(expectedCharge, Debit(entry, Accounts.CashStoreFor(store, "stripe"))); // gross = charge
        Assert.Equal(expectedTax, Credit(entry, $"tax.store-{store:N}"));
        Assert.Equal(expectedCharge - expectedTax, Credit(entry, $"revenue.store-{store:N}"));
        AssertWellFormed(entry, "USD");
    }

    [Fact]
    public async Task A_zero_decimal_currency_adds_whole_units_of_tax()
    {
        await using var db = NewDb();
        var store = AddStore(db, 825, inclusive: false);
        var subscription = await StartAsync(db, store, 999, "JPY", taxInclusive: false);
        var (service, provider, _) = Service(db);

        await service.RenewAsync(TenantId, subscription.Id, default);

        Assert.Equal(1081, Assert.Single(provider.Requests).AmountMinor); // ¥999 + ¥82 (82.4175 → 82)
        var entry = await EntryAsync(db, $"renew-{subscription.Id}-2");
        Assert.Equal(82, Credit(entry, $"tax.store-{store:N}"));
        AssertWellFormed(entry, "JPY");
    }

    [Fact]
    public async Task A_subscription_from_before_the_terms_snapshot_follows_the_stores_projected_regime()
    {
        await using var db = NewDb();
        var store = AddStore(db, 825, inclusive: false);
        var subscription = await StartAsync(db, store, 1000, "USD", taxInclusive: null);
        var (service, provider, _) = Service(db);

        await service.RenewAsync(TenantId, subscription.Id, default);

        Assert.Equal(1083, Assert.Single(provider.Requests).AmountMinor);
    }

    [Fact]
    public async Task An_unknown_rate_charges_the_bare_price_posts_no_tax_and_logs_an_error_on_an_exclusive_store()
    {
        // Decision: never a guessed rate, never a refused/dunned renewal for want of our own projection — the bare
        // price is charged (the tax is under-collected) and an ERROR names the store so its config is re-published.
        await using var db = NewDb();
        var store = AddStore(db, null, inclusive: false);
        var subscription = await StartAsync(db, store, 1000, "USD", taxInclusive: false);
        var (service, provider, log) = Service(db);

        var renewed = await service.RenewAsync(TenantId, subscription.Id, default);

        Assert.Equal(SubscriptionStatus.Active, renewed!.Status);                 // renewed, not dunned
        Assert.Equal(1000, Assert.Single(provider.Requests).AmountMinor);
        var entry = await EntryAsync(db, $"renew-{subscription.Id}-2");
        Assert.DoesNotContain(entry.Lines, l => l.AccountCode == $"tax.store-{store:N}");
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("under-collected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_storefronts_sharing_a_currency_each_add_their_own_rate()
    {
        await using var db = NewDb();
        var texas = AddStore(db, 825, inclusive: false);
        var oregon = AddStore(db, 0, inclusive: false);
        var nyc = AddStore(db, 888, inclusive: false);
        var a = await StartAsync(db, texas, 1000, "USD", taxInclusive: false);
        var b = await StartAsync(db, oregon, 1000, "USD", taxInclusive: false);
        var c = await StartAsync(db, nyc, 1000, "USD", taxInclusive: false);
        var (service, provider, _) = Service(db);

        await service.RenewAsync(TenantId, a.Id, default);
        await service.RenewAsync(TenantId, b.Id, default);
        await service.RenewAsync(TenantId, c.Id, default);

        Assert.Equal([1083L, 1000L, 1089L], provider.Requests.Select(r => r.AmountMinor)); // 88.8 → 89
        Assert.Equal(89, Credit(await EntryAsync(db, $"renew-{c.Id}-2"), $"tax.store-{nyc:N}"));
        Assert.DoesNotContain((await EntryAsync(db, $"renew-{a.Id}-2")).Lines, l => l.AccountCode.Contains($"{nyc:N}", StringComparison.Ordinal));
        var all = await db.JournalLines.AsNoTracking().ToListAsync();
        Assert.Equal(all.Sum(l => l.DebitMinor), all.Sum(l => l.CreditMinor)); // trial balance 0
    }

    [Theory]
    [InlineData(1000, 825, false, 1083, 83)]  // exclusive: added
    [InlineData(1100, 1000, true, 1100, 100)] // inclusive: contained
    [InlineData(1100, 0, false, 1100, 0)]     // 0%
    public async Task A_usage_charge_charges_the_rail_price_plus_added_tax_once(
        long amount, int bps, bool inclusive, long expectedCharge, long expectedTax)
    {
        await using var db = NewDb();
        var store = AddStore(db, bps, inclusive);
        var provider = new RecordingProvider();
        var reference = $"overage-{Guid.NewGuid():N}";

        for (var delivery = 0; delivery < 2; delivery++) // a redelivered message never charges or posts twice
        {
            await UsageBilling.For(db, provider).ChargeAsync(UsageBilling.Request(store, amount, reference), default);
        }

        Assert.Equal(expectedCharge, Assert.Single(provider.Requests).AmountMinor);
        var entry = await EntryAsync(db, reference);
        Assert.Equal(expectedCharge, Debit(entry, Accounts.CashStoreFor(store, "stripe")));
        Assert.Equal(expectedTax, Credit(entry, $"tax.store-{store:N}"));
        Assert.Equal(expectedCharge - expectedTax, Credit(entry, $"revenue.store-{store:N}"));
        AssertWellFormed(entry, "USD");
    }

    [Fact]
    public async Task A_renewal_retried_after_a_rate_change_charges_the_SAME_amount_under_the_same_key()
    {
        // A provider rejects a reused idempotency key with different parameters (Stripe idempotency_error), so a retry of
        // the same period must re-send exactly what the first attempt sent — the amount is pinned per period key.
        await using var db = NewDb();
        var store = AddStore(db, 825, inclusive: false);
        var subscription = await StartAsync(db, store, 1000, "USD", taxInclusive: false);
        var provider = new RecordingProvider { ThrowFirst = 1 };
        var service = new SubscriptionService(db, new SingleProviderRegistry(provider), Resolver(), new FixedTime(Now.AddMonths(1)),
            new ListLogger<SubscriptionService>(), new RecordingPublisher());

        var failed = await service.RenewAsync(TenantId, subscription.Id, default);  // provider down → dunned
        Assert.Equal(SubscriptionStatus.PastDue, failed!.Status);
        await SetRateAsync(db, store, 1000);                                         // the rate rises before the retry
        await service.RenewAsync(TenantId, subscription.Id, default);                // the same period, retried

        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(provider.Requests[0].IdempotencyKey, provider.Requests[1].IdempotencyKey);
        Assert.All(provider.Requests, r => Assert.Equal(1083, r.AmountMinor));       // pinned at 8.25%, not re-priced at 10%
        var entry = await EntryAsync(db, $"renew-{subscription.Id}-2");
        Assert.Equal(83, Credit(entry, $"tax.store-{store:N}"));                    // the split posted == the split charged
        Assert.Equal(1083, Debit(entry, Accounts.CashStoreFor(store, "stripe")));
        Assert.Equal(1083, (await RenewalAsync(db, subscription.Id, 2)).AmountMinor);
    }

    [Fact]
    public async Task A_nightly_run_commits_each_renewal_before_the_next_provider_call_and_a_failed_one_stays_retryable()
    {
        // Per-renewal commit is the deliberate semantics (ADR-0057 amendment): money moved for renewal 1 is durable
        // before renewal 2's card is touched; a provider failure on renewal 2 duns only it, renewal 3 still commits,
        // and renewal 2 retries later under the same key for the same pinned amount.
        var name = $"rca_{Guid.NewGuid():N}";
        await using var db = NewDb(name);
        var store = AddStore(db, 825, inclusive: false);
        var subs = new List<Subscription>();
        for (var i = 0; i < 3; i++)
        {
            subs.Add(await StartAsync(db, store, 1000, "USD", taxInclusive: false));
        }

        var committedBeforeCall = new List<(int Renewed, int PastDue)>();
        var provider = new RecordingProvider
        {
            OnAuthorize = call =>
            {
                // What ANOTHER connection sees at the moment of each provider call = what is committed.
                using var probe = NewDb(name);
                committedBeforeCall.Add((
                    probe.JournalEntries.Count(e => e.Reference.StartsWith("renew-")),
                    probe.Subscriptions.Count(s => s.Status == SubscriptionStatus.PastDue)));
                if (call == 2)
                {
                    throw new HttpRequestException("provider unavailable");
                }
            },
        };
        // 13:00 on the day the first periods end: past the schedule's default 12:00 run time.
        var nightly = new SubscriptionService(db, new SingleProviderRegistry(provider), Resolver(),
            new FixedTime(Now.AddMonths(1).AddHours(13)), new ListLogger<SubscriptionService>(), new RecordingPublisher());

        await nightly.AutoRenewDueAsync(default);

        Assert.Equal([(0, 0), (1, 0), (1, 1)], committedBeforeCall); // 1 committed before call 2; 2's dunning before call 3
        await using (var fresh = NewDb(name))
        {
            var statuses = await fresh.Subscriptions.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Status);
            var failed = provider.Requests[1].IdempotencyKey;
            var failedSub = subs.Single(s => failed.StartsWith($"renew-{s.Id}-", StringComparison.Ordinal));
            Assert.Equal(SubscriptionStatus.PastDue, statuses[failedSub.Id]);
            Assert.Equal(2, statuses.Count(kv => kv.Value == SubscriptionStatus.Active));
            Assert.Equal(2, await fresh.JournalEntries.CountAsync(e => e.Reference.StartsWith("renew-")));

            // The failed renewal is retryable: same key, same pinned amount, then it commits.
            await using var retryDb = NewDb(name);
            var retry = new SubscriptionService(retryDb, new SingleProviderRegistry(provider), Resolver(),
                new FixedTime(Now.AddMonths(1).AddHours(14)), new ListLogger<SubscriptionService>(), new RecordingPublisher());
            await retry.RenewAsync(TenantId, failedSub.Id, default);
            Assert.Equal(failed, provider.Requests[^1].IdempotencyKey);
            Assert.All(provider.Requests, r => Assert.Equal(1083, r.AmountMinor));
        }

        await using var final = NewDb(name);
        Assert.Equal(3, await final.JournalEntries.CountAsync(e => e.Reference.StartsWith("renew-")));
        Assert.All(await final.Subscriptions.AsNoTracking().ToListAsync(), s => Assert.Equal(SubscriptionStatus.Active, s.Status));
    }

    [Fact]
    public async Task The_next_period_is_a_new_key_and_takes_the_rate_then_in_force()
    {
        await using var db = NewDb();
        var store = AddStore(db, 825, inclusive: false);
        var subscription = await StartAsync(db, store, 1000, "USD", taxInclusive: false);
        var (service, provider, _) = Service(db);

        await service.RenewAsync(TenantId, subscription.Id, default);
        await SetRateAsync(db, store, 1000);
        await service.RenewAsync(TenantId, subscription.Id, default);

        Assert.Equal([1083L, 1100L], provider.Requests.Select(r => r.AmountMinor));
        Assert.NotEqual(provider.Requests[0].IdempotencyKey, provider.Requests[1].IdempotencyKey);
    }

    [Fact]
    public async Task A_usage_charge_redelivered_after_a_rate_change_charges_the_same_amount_and_posts_once()
    {
        await using var db = NewDb();
        var store = AddStore(db, 825, inclusive: false);
        var provider = new RecordingProvider { ThrowFirst = 1 };
        var registry = new SingleProviderRegistry(provider);
        const string reference = "overage-retry-1";

        Task Deliver() => UsageBilling.For(db, provider).ChargeAsync(UsageBilling.Request(store, 1000, reference), default);

        await Deliver();                                           // first delivery: the provider call blows up (unanswered: past due, same key kept)
        await SetRateAsync(db, store, 1000);                       // the rate changes before the redelivery
        await Deliver();                                           // redelivery
        await Deliver();                                           // a duplicate after success

        Assert.Equal(2, provider.Requests.Count);                  // the duplicate never reached the rail
        Assert.All(provider.Requests, r => Assert.Equal(reference, r.IdempotencyKey));
        Assert.All(provider.Requests, r => Assert.Equal(1083, r.AmountMinor));
        var entry = await EntryAsync(db, reference);               // SingleAsync: posted exactly once
        Assert.Equal(83, Credit(entry, $"tax.store-{store:N}"));
        Assert.Equal(1083, Debit(entry, Accounts.CashStoreFor(store, "stripe")));
    }

    [Fact]
    public async Task A_failed_usage_charge_posts_nothing()
    {
        await using var db = NewDb();
        var store = AddStore(db, 825, inclusive: false);
        var provider = new RecordingProvider { Outcome = PaymentOutcome.Failed };

        await UsageBilling.For(db, provider).ChargeAsync(UsageBilling.Request(store, 1000, "autoload-x-1", UsageChargeKind.AutoLoad), default);

        Assert.Equal(1083, Assert.Single(provider.Requests).AmountMinor);
        Assert.Empty(await db.JournalEntries.ToListAsync());
    }

    // ---- Helpers --------------------------------------------------------------------------------------

    private static PaymentsDbContext NewDb(string? name = null) =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseInMemoryDatabase(name ?? $"rca_{Guid.NewGuid():N}").Options);

    private static PaymentModeResolver Resolver() =>
        new(PaymentTestSupport.Config(("Payments:Mode", "Sandbox")), PaymentTestSupport.Env(Environments.Development));

    private static (SubscriptionService Service, RecordingProvider Provider, ListLogger<SubscriptionService> Log) Service(PaymentsDbContext db)
    {
        var provider = new RecordingProvider();
        var log = new ListLogger<SubscriptionService>();
        return (new SubscriptionService(db, new SingleProviderRegistry(provider), Resolver(), new FixedTime(Now.AddMonths(1)), log, new RecordingPublisher()), provider, log);
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

    private static async Task<Subscription> StartAsync(
        PaymentsDbContext db, Guid store, long price, string currency, bool? taxInclusive, bool taxExempt = false)
    {
        var subscription = Subscription.Start(
            TenantId, Guid.CreateVersion7(), "renewal@example.com", Guid.CreateVersion7(), null, BillingPeriod.Monthly,
            price, currency, Now, "cus_1", "pm_1", store, taxInclusive, taxExempt);
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return subscription;
    }

    private static Task<SubscriptionRenewal> RenewalAsync(PaymentsDbContext db, Guid subscriptionId, int sequence) =>
        db.SubscriptionRenewals.AsNoTracking().SingleAsync(r => r.SubscriptionId == subscriptionId && r.Sequence == sequence);

    private static Task<JournalEntry> EntryAsync(PaymentsDbContext db, string reference) =>
        db.JournalEntries.AsNoTracking().Include(e => e.Lines).SingleAsync(e => e.Reference == reference);

    private static long Credit(JournalEntry e, string account) => e.Lines.Where(l => l.AccountCode == account).Sum(l => l.CreditMinor);

    private static long Debit(JournalEntry e, string account) => e.Lines.Where(l => l.AccountCode == account).Sum(l => l.DebitMinor);

    private static void AssertWellFormed(JournalEntry entry, string currency)
    {
        Assert.Equal(entry.Lines.Sum(l => l.DebitMinor), entry.Lines.Sum(l => l.CreditMinor));
        Assert.All(entry.Lines, l => Assert.Equal(currency, l.Currency));
        Assert.All(entry.Lines, l => Assert.True((l.DebitMinor > 0) ^ (l.CreditMinor > 0))); // ck_line_one_side
        Assert.DoesNotContain(entry.Lines, l => Accounts.IsSharedCode(l.AccountCode));
    }
}
