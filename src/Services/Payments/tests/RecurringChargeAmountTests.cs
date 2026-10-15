using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
            await UsageChargeBilling.ChargeAndPostAsync(
                db, new SingleProviderRegistry(provider), Resolver(), TimeProvider.System, new ListLogger<RecurringChargeAmountTests>(),
                store, amount, "USD", reference, $"Usage overage — {MeterType.Token} × 20", default);
        }

        Assert.Equal(expectedCharge, Assert.Single(provider.Requests).AmountMinor);
        var entry = await EntryAsync(db, reference);
        Assert.Equal(expectedCharge, Debit(entry, Accounts.CashStoreFor(store, "stripe")));
        Assert.Equal(expectedTax, Credit(entry, $"tax.store-{store:N}"));
        Assert.Equal(expectedCharge - expectedTax, Credit(entry, $"revenue.store-{store:N}"));
        AssertWellFormed(entry, "USD");
    }

    [Fact]
    public async Task A_failed_usage_charge_posts_nothing()
    {
        await using var db = NewDb();
        var store = AddStore(db, 825, inclusive: false);
        var provider = new RecordingProvider { Outcome = PaymentOutcome.Failed };

        await UsageChargeBilling.ChargeAndPostAsync(
            db, new SingleProviderRegistry(provider), Resolver(), TimeProvider.System, new ListLogger<RecurringChargeAmountTests>(),
            store, 1000, "USD", "autoload-x-1", "Usage auto-load", default);

        Assert.Equal(1083, Assert.Single(provider.Requests).AmountMinor);
        Assert.Empty(await db.JournalEntries.ToListAsync());
    }

    // ---- Helpers --------------------------------------------------------------------------------------

    private static PaymentsDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseInMemoryDatabase($"rca_{Guid.NewGuid():N}").Options);

    private static PaymentModeResolver Resolver() =>
        new(PaymentTestSupport.Config(("Payments:Mode", "Sandbox")), PaymentTestSupport.Env(Environments.Development));

    private static (SubscriptionService Service, RecordingProvider Provider, ListLogger<SubscriptionService> Log) Service(PaymentsDbContext db)
    {
        var provider = new RecordingProvider();
        var log = new ListLogger<SubscriptionService>();
        return (new SubscriptionService(db, new SingleProviderRegistry(provider), Resolver(), new FixedTime(Now.AddMonths(1)), log), provider, log);
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

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingProvider : IPaymentProvider
    {
        public List<PaymentRequest> Requests { get; } = [];
        public PaymentOutcome Outcome { get; init; } = PaymentOutcome.Succeeded;
        public string ProviderKey => "stripe";

        public Task<PaymentResponse> AuthorizeAsync(PaymentRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new PaymentResponse($"pi_rec_{Requests.Count}", null, Outcome));
        }

        public Task<string> CreateCustomerAsync(Guid userId, string email, CancellationToken ct) => throw new NotSupportedException();
        public Task<SetupIntentResult> CreateSetupIntentAsync(string providerCustomerId, CancellationToken ct) => throw new NotSupportedException();
        public Task<SavedPaymentMethodDetails> GetPaymentMethodAsync(string providerPaymentMethodId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProviderRefundResult> RefundAsync(string paymentIntentId, long amountMinor, string idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
        public PaymentWebhookEvent? ParseWebhook(string payload, string signatureHeader, IReadOnlyList<string> secrets) => null;
    }

    private sealed class SingleProviderRegistry(IPaymentProvider provider) : IPaymentProviderRegistry
    {
        public IPaymentProvider Resolve(PaymentAccountSnapshot account) => provider;
        public IPaymentProvider ResolveDefault() => provider;
        public IPaymentProvider ResolveByKey(string providerKey) => provider;
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
