using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// usage_charge_failure_feedback (owner decision 2026-10-27): a usage charge that does not settle is dunned exactly like a
/// subscription renewal — retried on days 1, 3 and 7 after the first failure, each retry under a FRESH provider key with
/// the SAME pinned amount, then abandoned — and every answered failure is reported back (<see cref="UsageChargeFailed"/>:
/// when it is retried, when it is abandoned, whether this was the final retry). A provider that never answered duns
/// silently and keeps its key. Money arriving later for an attempt settles the charge once.
/// </summary>
public class UsageChargeDunningTests
{
    private static readonly DateTimeOffset FirstFailure = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_declined_charge_is_retried_on_days_1_3_and_7_with_fresh_keys_and_the_same_amount_then_abandoned()
    {
        var name = $"ucd_{Guid.NewGuid():N}";
        await using var db = NewDb(name);
        var store = AddStore(db, 825);
        var provider = new RecordingProvider { Outcome = PaymentOutcome.Failed };
        var published = new List<object>();
        const string reference = "overage-ucd-1";

        await Billing(name, provider, published, FirstFailure).ChargeAsync(UsageBilling.Request(store, 1000, reference), default);
        var first = Assert.IsType<UsageChargeFailed>(Assert.Single(published));
        Assert.Equal((0, false, ChargeFailureReason.Failed), (first.Attempt, first.Final, first.Reason));
        Assert.Equal((FirstFailure.AddDays(1), FirstFailure.AddDays(7)), (first.NextRetryAt, first.AbandonsAfter));

        await SetRateAsync(db, store, 1000); // a rate change never changes what the charge asks for
        foreach (var hours in new[] { 12, 25, 26, 73, 169, 200 }) // nothing before day 1; each day once; nothing after day 7
        {
            await Billing(name, provider, published, FirstFailure.AddHours(hours)).RetryDueAsync(default);
        }

        Assert.Equal([reference, $"{reference}-a1", $"{reference}-a2", $"{reference}-a3"], provider.Requests.Select(r => r.IdempotencyKey));
        Assert.All(provider.Requests, r => Assert.Equal(1083, r.AmountMinor));
        var failures = published.Cast<UsageChargeFailed>().ToList();
        Assert.Equal([0, 1, 2, 3], failures.Select(f => f.Attempt));
        Assert.Equal([FirstFailure.AddDays(1), FirstFailure.AddDays(3), FirstFailure.AddDays(7), null], failures.Select(f => f.NextRetryAt));
        Assert.Equal([false, false, false, true], failures.Select(f => f.Final));
        await using var fresh = NewDb(name);
        Assert.Equal(UsageChargeStatus.Abandoned, (await fresh.UsageCharges.SingleAsync()).Status);
        Assert.Empty(await fresh.JournalEntries.ToListAsync());
    }

    [Fact]
    public async Task A_retry_that_settles_posts_once_announces_the_settlement_and_stops_the_schedule()
    {
        var name = $"ucd_{Guid.NewGuid():N}";
        await using var db = NewDb(name);
        var store = AddStore(db, 825);
        var provider = new RecordingProvider();
        provider.Outcomes.Enqueue(PaymentOutcome.Failed);
        var published = new List<object>();
        const string reference = "autoload-ucd-2";

        await Billing(name, provider, published, FirstFailure).ChargeAsync(UsageBilling.Request(store, 1000, reference, UsageChargeKind.AutoLoad), default);
        await Billing(name, provider, published, FirstFailure.AddDays(1).AddHours(1)).RetryDueAsync(default);
        await Billing(name, provider, published, FirstFailure.AddDays(3).AddHours(1)).RetryDueAsync(default);

        Assert.Equal(2, provider.Requests.Count);
        Assert.IsType<UsageChargeFailed>(published[0]);
        var settled = Assert.IsType<UsageChargeSettled>(published[1]);
        Assert.Equal((reference, 1, 1083L), (settled.Reference, settled.Attempt, settled.GrossMinor));
        await using var fresh = NewDb(name);
        Assert.Equal(reference, Assert.Single(await fresh.JournalEntries.ToListAsync()).Reference);
        Assert.Equal(UsageChargeStatus.Settled, (await fresh.UsageCharges.SingleAsync()).Status);
    }

    [Fact]
    public async Task A_provider_that_never_answers_duns_silently_and_the_next_try_reuses_its_key()
    {
        var name = $"ucd_{Guid.NewGuid():N}";
        await using var db = NewDb(name);
        var store = AddStore(db, 0);
        var provider = new RecordingProvider { ThrowFirst = 1 };
        var published = new List<object>();
        const string reference = "overage-ucd-3";

        await Billing(name, provider, published, FirstFailure).ChargeAsync(UsageBilling.Request(store, 1000, reference), default);
        Assert.Empty(published);                                               // no customer-visible failure, Usage keeps its credit
        await using (var mid = NewDb(name))
        {
            var charge = await mid.UsageCharges.SingleAsync();
            Assert.Equal((UsageChargeStatus.PastDue, "Error"), (charge.Status, charge.LastFailureReason));
        }

        await Billing(name, provider, published, FirstFailure.AddDays(1).AddHours(1)).RetryDueAsync(default);
        Assert.Equal([reference, reference], provider.Requests.Select(r => r.IdempotencyKey)); // same key: the provider de-duplicates
        Assert.IsType<UsageChargeSettled>(Assert.Single(published));
    }

    [Fact]
    public async Task Money_arriving_later_for_an_attempt_settles_the_charge_once_even_after_it_was_abandoned()
    {
        var name = $"ucd_{Guid.NewGuid():N}";
        await using var db = NewDb(name);
        var store = AddStore(db, 825);
        var provider = new RecordingProvider { Outcome = PaymentOutcome.RequiresAction };
        var published = new List<object>();
        const string reference = "autoload-ucd-4";

        await Billing(name, provider, published, FirstFailure).ChargeAsync(UsageBilling.Request(store, 1000, reference, UsageChargeKind.AutoLoad), default);
        foreach (var day in new[] { 1, 3, 7 })
        {
            await Billing(name, provider, published, FirstFailure.AddDays(day).AddHours(1)).RetryDueAsync(default);
        }

        Assert.True(published.OfType<UsageChargeFailed>().Last().Final);
        var intent = (await NewDb(name).UsageChargeAttempts.SingleAsync(a => a.Attempt == 0)).PaymentIntentId!;
        for (var delivery = 0; delivery < 2; delivery++) // the provider's success event, delivered twice
        {
            await using var webhookDb = NewDb(name);
            Assert.True(await UsageBilling.For(webhookDb, provider, new FixedTime(FirstFailure.AddDays(8)), Recorder(published)).SettleAttemptAsync(intent, default));
            await webhookDb.SaveChangesAsync();
        }

        Assert.Single(published.OfType<UsageChargeSettled>());
        await using var fresh = NewDb(name);
        Assert.Equal(UsageChargeStatus.Settled, (await fresh.UsageCharges.SingleAsync()).Status);
        Assert.Single(await fresh.JournalEntries.ToListAsync());
        Assert.False(await UsageBilling.For(fresh, provider).SettleAttemptAsync("pi_not_a_usage_charge", default));
    }

    [Fact]
    public void Subscriptions_and_usage_charges_share_one_schedule()
    {
        Assert.Same(DunningSchedule.RetryDays, Subscription.RetryDays);
        Assert.Equal([1, 3, 7], DunningSchedule.RetryDays);
        var start = FirstFailure;
        Assert.Null(DunningSchedule.Due(start, start, start.AddHours(23)));
        Assert.Equal((1, false), DunningSchedule.Due(start, start, start.AddDays(1)));
        Assert.Equal((3, true), DunningSchedule.Due(start, start, start.AddDays(30)));         // missed days collapse into the last
        Assert.Null(DunningSchedule.Due(start, start.AddDays(7), start.AddDays(30)));
        Assert.Equal("k", DunningSchedule.AttemptKey("k", 0));
        Assert.Equal("k-a2", DunningSchedule.AttemptKey("k", 2));
    }

    // ---- Helpers --------------------------------------------------------------------------------------

    private static PaymentsDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseInMemoryDatabase(name).Options);

    /// <summary>A fresh context + service per run (as the job / consumer scope gets), appending what it publishes.</summary>
    private static Infrastructure.Consumers.UsageChargeBilling Billing(string name, RecordingProvider provider, List<object> published, DateTimeOffset at) =>
        UsageBilling.For(NewDb(name), provider, new FixedTime(at), Recorder(published));

    private static RecordingPublisher Recorder(List<object> sink) => new() { Published = sink };

    private static Guid AddStore(PaymentsDbContext db, int? bps)
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
            TaxInclusive = false,
            UpdatedAt = FirstFailure,
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
}
