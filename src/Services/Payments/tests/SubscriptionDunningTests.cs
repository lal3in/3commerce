using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;
using ThreeCommerce.Payments.Infrastructure;
using ThreeCommerce.Payments.Infrastructure.Providers;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// subscription_past_due_retry_and_authenticate (owner decisions 2026-10-27, ADR-0057 amendment): the nightly job retries
/// a PastDue subscription on days 1, 3 and 7 after the first failure, then cancels it and emails the customer; every
/// retry uses a FRESH attempt key (<c>{periodKey}-a{n}</c>) with the SAME pinned amount; the customer can pay on the
/// storefront page (on-session, optionally another saved card); and a 3-D Secure attempt settles later from the
/// provider's success event — exactly once.
/// </summary>
public class SubscriptionDunningTests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);

    // The first failure happens at the period end (Now + 1 month) at 13:00 — past the default 12:00 nightly run time.
    private static readonly DateTimeOffset FirstFailure = Now.AddMonths(1).AddHours(13);

    [Fact]
    public void The_schedule_is_days_1_3_and_7_and_an_attempt_in_a_window_counts_for_it()
    {
        var sub = Subscription.Start(TenantId, Guid.NewGuid(), "a@example.com", Guid.NewGuid(), null, BillingPeriod.Monthly, 1000, "USD", Now);
        Assert.Null(sub.ScheduledRetryDue(FirstFailure));                         // in good standing: nothing to retry

        sub.AttemptAnswered(FirstFailure);
        sub.MarkPastDue(FirstFailure, "Failed");
        Assert.Equal(FirstFailure.AddDays(1), sub.NextRetryAt);
        Assert.Equal(FirstFailure.AddDays(7), sub.CancelsAfter);
        Assert.Null(sub.ScheduledRetryDue(FirstFailure.AddHours(23)));            // not before day 1
        Assert.Equal((1, false), sub.ScheduledRetryDue(FirstFailure.AddDays(1)));

        sub.AttemptAnswered(FirstFailure.AddDays(2));                             // the customer tried on day 2
        Assert.Null(sub.ScheduledRetryDue(FirstFailure.AddDays(2.5)));            // day 1's retry is covered
        Assert.Equal(FirstFailure.AddDays(3), sub.NextRetryAt);
        Assert.Equal((2, false), sub.ScheduledRetryDue(FirstFailure.AddDays(3)));
        Assert.Equal((3, true), sub.ScheduledRetryDue(FirstFailure.AddDays(9))); // missed days collapse into one (the last)

        Assert.Equal("Failed", sub.LastFailureReason);
        sub.MarkPastDue(FirstFailure.AddDays(3), "RequiresAction");               // a later failure keeps the start
        Assert.Equal(FirstFailure.AddDays(7), sub.CancelsAfter);
    }

    [Fact]
    public async Task Retries_fall_on_days_1_3_and_7_with_fresh_keys_and_the_pinned_amount_then_the_subscription_is_cancelled()
    {
        var name = $"dun_{Guid.NewGuid():N}";
        await using var db = NewDb(name);
        var store = AddStore(db, 825, inclusive: false);
        var subscription = await StartAsync(db, store, 1000);
        var provider = new RecordingProvider { Outcome = PaymentOutcome.Failed };
        var publisher = new RecordingPublisher();

        await RunNightlyAsync(name, provider, publisher, FirstFailure);              // the renewal is declined
        await SetRateAsync(db, store, 1000);                                       // a rate change never re-prices the period
        foreach (var day in new[] { 0.5, 1, 2, 3, 5, 7 })
        {
            await RunNightlyAsync(name, provider, publisher, FirstFailure.AddDays(day));
        }

        var key = $"renew-{subscription.Id}-{subscription.CurrentPeriodEnd:O}";
        Assert.Equal([key, key + "-a1", key + "-a2", key + "-a3"], provider.Requests.Select(r => r.IdempotencyKey));
        Assert.All(provider.Requests, r => Assert.Equal(1083, r.AmountMinor));
        await using var fresh = NewDb(name);
        var stored = await fresh.Subscriptions.AsNoTracking().SingleAsync();
        Assert.Equal(SubscriptionStatus.Cancelled, stored.Status);
        Assert.Equal(subscription.CurrentPeriodEnd, stored.CurrentPeriodEnd);       // never advanced
        Assert.Empty(await fresh.JournalEntries.ToListAsync());                     // nothing posted
        Assert.Equal(4, await fresh.SubscriptionChargeAttempts.CountAsync());

        var failures = publisher.Published.OfType<SubscriptionPaymentFailed>().ToList();
        Assert.Equal(
            [FirstFailure.AddDays(1), FirstFailure.AddDays(3), FirstFailure.AddDays(7)],
            failures.Select(f => f.NextRetryAt!.Value));
        var cancelled = Assert.Single(publisher.Published.OfType<SubscriptionCancelledForNonPayment>());
        Assert.Equal((subscription.Id, 1083L, "renewal@example.com"), (cancelled.SubscriptionId, cancelled.AmountMinor, cancelled.CustomerEmail));

        await RunNightlyAsync(name, provider, publisher, FirstFailure.AddDays(30)); // cancelled: never charged again
        Assert.Equal(4, provider.Requests.Count);
    }

    [Fact]
    public async Task A_retry_that_settles_renews_once_ends_dunning_and_stops_the_schedule()
    {
        var name = $"dun_{Guid.NewGuid():N}";
        await using var db = NewDb(name);
        var store = AddStore(db, 825, inclusive: false);
        var subscription = await StartAsync(db, store, 1000);
        var provider = new RecordingProvider { Outcome = PaymentOutcome.Succeeded };
        provider.Outcomes.Enqueue(PaymentOutcome.Failed);
        provider.Outcomes.Enqueue(PaymentOutcome.RequiresAction);
        var publisher = new RecordingPublisher();

        await RunNightlyAsync(name, provider, publisher, FirstFailure);               // declined
        await RunNightlyAsync(name, provider, publisher, FirstFailure.AddDays(1));    // needs 3-D Secure
        await RunNightlyAsync(name, provider, publisher, FirstFailure.AddDays(3));    // settles
        await RunNightlyAsync(name, provider, publisher, FirstFailure.AddDays(7));    // nothing left to retry

        Assert.Equal(3, provider.Requests.Count);
        await using var fresh = NewDb(name);
        var stored = await fresh.Subscriptions.AsNoTracking().SingleAsync();
        Assert.Equal(SubscriptionStatus.Active, stored.Status);
        Assert.Null(stored.DunningStartedAt);
        Assert.Null(stored.LastFailureReason);
        Assert.Equal(subscription.CurrentPeriodEnd.AddMonths(1), stored.CurrentPeriodEnd);
        var entry = await fresh.JournalEntries.AsNoTracking().Include(e => e.Lines).SingleAsync();
        Assert.Equal($"renew-{subscription.Id}-2", entry.Reference);
        Assert.Equal(83, entry.Lines.Where(l => l.AccountCode == $"tax.store-{store:N}").Sum(l => l.CreditMinor));
        Assert.IsType<SubscriptionRenewalActionRequired>(publisher.Published[1]);
        Assert.Empty(publisher.Published.OfType<SubscriptionCancelledForNonPayment>());
    }

    [Fact]
    public async Task A_provider_outage_reuses_the_unanswered_key_sends_no_email_and_does_not_use_up_a_retry()
    {
        var name = $"dun_{Guid.NewGuid():N}";
        await using var db = NewDb(name);
        var store = AddStore(db, 0, inclusive: false);
        var subscription = await StartAsync(db, store, 1000);
        var provider = new RecordingProvider { Outcome = PaymentOutcome.Failed, ThrowFirst = 2 };
        var publisher = new RecordingPublisher();

        await RunNightlyAsync(name, provider, publisher, FirstFailure);              // throws: unanswered
        await RunNightlyAsync(name, provider, publisher, FirstFailure.AddDays(1));   // throws again: same key
        await RunNightlyAsync(name, provider, publisher, FirstFailure.AddDays(2));   // day 1's retry is still due: answered

        var key = $"renew-{subscription.Id}-{subscription.CurrentPeriodEnd:O}";
        Assert.Equal([key, key, key], provider.Requests.Select(r => r.IdempotencyKey)); // the provider de-duplicates
        Assert.Single(publisher.Published.OfType<SubscriptionPaymentFailed>());         // only the answered decline emails
        await using var fresh = NewDb(name);
        var stored = await fresh.Subscriptions.AsNoTracking().SingleAsync();
        Assert.Equal(SubscriptionStatus.PastDue, stored.Status);
        Assert.Equal(FirstFailure.AddDays(3), stored.NextRetryAt);
    }

    [Fact]
    public async Task The_customer_pays_on_session_with_another_saved_card_which_then_renews_the_subscription()
    {
        var name = $"dun_{Guid.NewGuid():N}";
        await using var db = NewDb(name);
        var store = AddStore(db, 825, inclusive: false);
        var subscription = await StartAsync(db, store, 1000);
        var (userId, methodId) = AddSavedCard(db, "pm_new_card", "cus_new");
        var provider = new RecordingProvider { Outcome = PaymentOutcome.Failed };
        await RunNightlyAsync(name, provider, new RecordingPublisher(), FirstFailure);

        provider.Outcome = PaymentOutcome.Succeeded;
        var publisher = new RecordingPublisher();
        await using var payDb = NewDb(name);
        var result = await Service(payDb, provider, publisher, FirstFailure.AddHours(2))
            .PayAsync(TenantId, "Renewal@Example.com", userId, subscription.Id, methodId, default);

        Assert.Equal(SubscriptionPayOutcome.Paid, result!.Outcome);
        var request = provider.Requests[^1];
        Assert.True(request.CustomerPresent);                                       // on-session: the bank may ask for 3-D Secure
        Assert.Equal(("pm_new_card", "cus_new"), (request.ProviderPaymentMethodId, request.ProviderCustomerId));
        Assert.EndsWith("-a1", request.IdempotencyKey);
        Assert.Equal(1083, request.AmountMinor);
        // No email (the customer sees the answer on the page) — only the settled renewal (ADR-0063 P4b), for the period paid.
        Assert.Equal(2, Assert.IsType<SubscriptionRenewalSettled>(Assert.Single(publisher.Published)).Sequence);
        await using var fresh = NewDb(name);
        var stored = await fresh.Subscriptions.AsNoTracking().SingleAsync();
        Assert.Equal(SubscriptionStatus.Active, stored.Status);
        Assert.Equal("pm_new_card", stored.ProviderPaymentMethodId);              // future renewals charge the new card
        Assert.Single(await fresh.JournalEntries.ToListAsync());
    }

    [Fact]
    public async Task The_pay_page_refuses_what_is_not_the_customers_or_not_past_due_and_an_unknown_card()
    {
        var name = $"dun_{Guid.NewGuid():N}";
        await using var db = NewDb(name);
        var store = AddStore(db, 0, inclusive: false);
        var subscription = await StartAsync(db, store, 1000);
        var provider = new RecordingProvider();
        var service = Service(db, provider, new RecordingPublisher(), FirstFailure);

        Assert.Null(await service.PayAsync(TenantId, "someone-else@example.com", Guid.NewGuid(), subscription.Id, null, default));
        Assert.Equal(SubscriptionPayOutcome.NotPastDue,
            (await service.PayAsync(TenantId, "renewal@example.com", Guid.NewGuid(), subscription.Id, null, default))!.Outcome);

        provider.Outcome = PaymentOutcome.Failed;
        await RunNightlyAsync(name, provider, new RecordingPublisher(), FirstFailure);
        await using var payDb = NewDb(name);
        var unknown = await Service(payDb, provider, new RecordingPublisher(), FirstFailure)
            .PayAsync(TenantId, "renewal@example.com", Guid.NewGuid(), subscription.Id, Guid.NewGuid(), default);
        Assert.Equal(SubscriptionPayOutcome.UnknownPaymentMethod, unknown!.Outcome);
        Assert.Single(provider.Requests);                                           // none of these charged anything
    }

    [Fact]
    public async Task A_3DS_attempt_settles_from_the_providers_success_event_exactly_once()
    {
        var name = $"dun_{Guid.NewGuid():N}";
        await using var db = NewDb(name);
        var store = AddStore(db, 825, inclusive: false);
        var subscription = await StartAsync(db, store, 1000);
        var provider = new RecordingProvider { Outcome = PaymentOutcome.Failed };
        await RunNightlyAsync(name, provider, new RecordingPublisher(), FirstFailure);

        provider.Outcome = PaymentOutcome.RequiresAction;
        await using var payDb = NewDb(name);
        var paid = await Service(payDb, provider, new RecordingPublisher(), FirstFailure.AddHours(1))
            .PayAsync(TenantId, "renewal@example.com", Guid.NewGuid(), subscription.Id, null, default);
        Assert.Equal(SubscriptionPayOutcome.RequiresAction, paid!.Outcome);
        Assert.NotNull(paid.PaymentIntentId);
        Assert.Equal(SubscriptionStatus.PastDue, paid.Subscription.Status);

        // The customer completes 3-D Secure; the provider's success event arrives — twice.
        for (var delivery = 0; delivery < 2; delivery++)
        {
            await using var webhookDb = NewDb(name);
            Assert.True(await Service(webhookDb, provider, new RecordingPublisher(), FirstFailure.AddHours(2))
                .SettleAttemptAsync(paid.PaymentIntentId!, default));
            await webhookDb.SaveChangesAsync();
        }

        await using var fresh = NewDb(name);
        var stored = await fresh.Subscriptions.AsNoTracking().SingleAsync();
        Assert.Equal(SubscriptionStatus.Active, stored.Status);
        Assert.Equal(subscription.CurrentPeriodEnd.AddMonths(1), stored.CurrentPeriodEnd);
        var entry = await fresh.JournalEntries.AsNoTracking().Include(e => e.Lines).SingleAsync();
        Assert.Equal(1083, entry.Lines.Where(l => l.AccountCode == Accounts.CashStoreFor(store, "stripe")).Sum(l => l.DebitMinor));
        Assert.NotNull((await fresh.SubscriptionChargeAttempts.SingleAsync(a => a.PaymentIntentId == paid.PaymentIntentId)).SettledAt);

        await using var other = NewDb(name);
        Assert.False(await Service(other, provider, new RecordingPublisher(), FirstFailure).SettleAttemptAsync("pi_not_a_renewal", default));
    }

    [Fact]
    public async Task Money_arriving_for_a_closed_period_is_not_booked()
    {
        var name = $"dun_{Guid.NewGuid():N}";
        await using var db = NewDb(name);
        var store = AddStore(db, 0, inclusive: false);
        var subscription = await StartAsync(db, store, 1000);
        var provider = new RecordingProvider { Outcome = PaymentOutcome.RequiresAction };
        await RunNightlyAsync(name, provider, new RecordingPublisher(), FirstFailure);      // attempt 0: needs 3-D Secure
        var stale = (await NewDb(name).SubscriptionChargeAttempts.SingleAsync()).PaymentIntentId!;
        provider.Outcome = PaymentOutcome.Succeeded;
        await RunNightlyAsync(name, provider, new RecordingPublisher(), FirstFailure.AddDays(1)); // the retry settles the period

        await using var webhookDb = NewDb(name);
        Assert.True(await Service(webhookDb, provider, new RecordingPublisher(), FirstFailure.AddDays(2)).SettleAttemptAsync(stale, default));
        await webhookDb.SaveChangesAsync();

        await using var fresh = NewDb(name);
        Assert.Single(await fresh.JournalEntries.ToListAsync());                            // the period is not booked twice
        Assert.Equal(subscription.CurrentPeriodEnd.AddMonths(1), (await fresh.Subscriptions.AsNoTracking().SingleAsync()).CurrentPeriodEnd);
    }

    // ---- Helpers --------------------------------------------------------------------------------------

    private static async Task RunNightlyAsync(string name, RecordingProvider provider, RecordingPublisher publisher, DateTimeOffset at)
    {
        await using var db = NewDb(name);
        await Service(db, provider, publisher, at).AutoRenewDueAsync(default);
    }

    private static PaymentsDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseInMemoryDatabase(name).Options);

    private static SubscriptionService Service(PaymentsDbContext db, RecordingProvider provider, RecordingPublisher publisher, DateTimeOffset at) =>
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

    private static (Guid UserId, Guid MethodId) AddSavedCard(PaymentsDbContext db, string providerMethodId, string providerCustomerId)
    {
        var userId = Guid.CreateVersion7();
        var customer = new PaymentCustomer
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            UserId = userId,
            Provider = "stripe",
            ProviderCustomerId = providerCustomerId,
            CreatedAt = Now,
        };
        var method = new SavedPaymentMethod
        {
            Id = Guid.CreateVersion7(),
            PaymentCustomerId = customer.Id,
            TenantId = TenantId,
            UserId = userId,
            Provider = "stripe",
            ProviderPaymentMethodId = providerMethodId,
            Brand = "visa",
            Last4 = "4242",
            ExpMonth = 12,
            ExpYear = 2030,
            CreatedAt = Now,
        };
        db.PaymentCustomers.Add(customer);
        db.SavedPaymentMethods.Add(method);
        db.SaveChanges();
        return (userId, method.Id);
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
