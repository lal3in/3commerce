using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// usage_charge_pay_page: the customer pays an unpaid usage charge from the storefront "complete payment" page — ON-SESSION
/// (3-D Secure possible), under a FRESH attempt key with the reference's PINNED amount, optionally on another of their own
/// saved cards. A paid charge posts and announces <see cref="UsageChargeSettled"/> once (Usage re-grants the credit and
/// lifts the hold); an abandoned charge is still payable (the usage was consumed); the customer's own decline sends no
/// email; 3-D Secure settles once from the provider's success event; nothing else is payable, and nobody else's charge or
/// card is reachable.
/// </summary>
public class UsageChargePayTests
{
    private static readonly DateTimeOffset FirstFailure = new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero).AddTicks(1_755_249);
    private static readonly Guid UserId = Guid.Parse("0196f000-0000-7000-8000-000000000001");
    private const string Email = "usage@example.com";

    [Fact]
    public async Task Paying_a_past_due_charge_charges_on_session_under_a_fresh_key_with_the_pinned_amount_and_settles_once()
    {
        var (name, provider, published, reference) = await PastDueAsync("overage-pay-1");

        var result = await Billing(name, provider, published, FirstFailure.AddHours(2)).PayAsync(UsageBilling.TenantId, "USAGE@example.com", UserId, reference, null, default);

        Assert.Equal(UsageChargePayOutcome.Paid, result!.Outcome);
        var customer = provider.Requests[^1];
        Assert.Equal(($"{reference}-a1", 1083L, true), (customer.IdempotencyKey, customer.AmountMinor, customer.CustomerPresent));
        Assert.Single(published.OfType<UsageChargeSettled>());
        Assert.Single(published.OfType<UsageChargeFailed>()); // only the original decline
        await using var fresh = NewDb(name);
        Assert.Equal(UsageChargeStatus.Settled, (await fresh.UsageCharges.SingleAsync()).Status);
        Assert.Equal(reference, Assert.Single(await fresh.JournalEntries.ToListAsync()).Reference);
        Assert.Equal(UsageChargeTrigger.Customer, (await fresh.UsageChargeAttempts.SingleAsync(a => a.Attempt == 1)).Trigger);

        var again = await Billing(name, provider, published, FirstFailure.AddHours(3)).PayAsync(UsageBilling.TenantId, Email, UserId, reference, null, default);
        Assert.Equal(UsageChargePayOutcome.NotPayable, again!.Outcome); // a second visit pays nothing
        Assert.Equal(2, provider.Requests.Count);
    }

    [Fact]
    public async Task The_customers_own_decline_is_shown_not_emailed_and_the_charge_stays_owed()
    {
        var (name, provider, published, reference) = await PastDueAsync("autoload-pay-2", PaymentOutcome.Failed, PaymentOutcome.Failed);

        var result = await Billing(name, provider, published, FirstFailure.AddHours(2)).PayAsync(UsageBilling.TenantId, Email, UserId, reference, null, default);

        Assert.Equal(UsageChargePayOutcome.Declined, result!.Outcome);
        Assert.Single(published.OfType<UsageChargeFailed>()); // the scheduled one only: no second email
        await using var fresh = NewDb(name);
        var charge = await fresh.UsageCharges.SingleAsync();
        Assert.Equal((UsageChargeStatus.PastDue, FirstFailure.AddHours(2).AddTicks(-9)), (charge.Status, charge.LastAttemptAt)); // counts as an attempt
    }

    [Fact]
    public async Task A_3d_secure_answer_is_settled_once_by_the_providers_success_event()
    {
        var (name, provider, published, reference) = await PastDueAsync("overage-pay-3", PaymentOutcome.Failed, PaymentOutcome.RequiresAction);

        var result = await Billing(name, provider, published, FirstFailure.AddHours(2)).PayAsync(UsageBilling.TenantId, Email, UserId, reference, null, default);
        Assert.Equal(UsageChargePayOutcome.RequiresAction, result!.Outcome);
        Assert.NotNull(result.PaymentIntentId);

        for (var delivery = 0; delivery < 2; delivery++)
        {
            await using var db = NewDb(name);
            Assert.True(await Billing(db, provider, published, FirstFailure.AddHours(3)).SettleAttemptAsync(result.PaymentIntentId!, default));
            await db.SaveChangesAsync();
        }

        Assert.Single(published.OfType<UsageChargeSettled>());
        await using var fresh = NewDb(name);
        Assert.Single(await fresh.JournalEntries.ToListAsync());
    }

    [Fact]
    public async Task An_abandoned_charge_can_still_be_paid()
    {
        var (name, provider, published, reference) = await PastDueAsync("autoload-pay-4");
        provider.Outcome = PaymentOutcome.Failed;
        foreach (var day in new[] { 1, 3, 7 })
        {
            await Billing(name, provider, published, FirstFailure.AddDays(day).AddHours(1)).RetryDueAsync(default);
        }

        await using (var mid = NewDb(name))
        {
            Assert.Equal(UsageChargeStatus.Abandoned, (await mid.UsageCharges.SingleAsync()).Status);
        }

        provider.Outcome = PaymentOutcome.Succeeded;
        var result = await Billing(name, provider, published, FirstFailure.AddDays(9)).PayAsync(UsageBilling.TenantId, Email, UserId, reference, null, default);

        Assert.Equal(UsageChargePayOutcome.Paid, result!.Outcome);
        Assert.Equal(1083, provider.Requests[^1].AmountMinor);
        Assert.Single(published.OfType<UsageChargeSettled>());
    }

    [Fact]
    public async Task Only_an_owed_charge_of_the_signed_in_customer_is_payable()
    {
        var name = $"ucp_{Guid.NewGuid():N}";
        var provider = new RecordingProvider();
        var published = new List<object>();
        await using (var db = NewDb(name))
        {
            AddStore(db);
        }

        await Billing(name, provider, published, FirstFailure).ChargeAsync(UsageBilling.Request(null, 900, "overage-settled"), default);
        await Billing(name, provider, published, FirstFailure).ChargeAsync(
            UsageBilling.Request(null, 900, "autoload-skipped", UsageChargeKind.AutoLoad), default);
        var calls = provider.Requests.Count;

        var billing = Billing(name, provider, published, FirstFailure.AddHours(1));
        Assert.Equal(UsageChargePayOutcome.NotPayable, (await billing.PayAsync(UsageBilling.TenantId, Email, UserId, "overage-settled", null, default))!.Outcome);
        Assert.Equal(UsageChargePayOutcome.NotPayable, (await billing.PayAsync(UsageBilling.TenantId, Email, UserId, "autoload-skipped", null, default))!.Outcome);
        Assert.Null(await billing.PayAsync(UsageBilling.TenantId, "someone-else@example.com", UserId, "overage-settled", null, default));
        Assert.Null(await billing.PayAsync(Guid.NewGuid(), Email, UserId, "overage-settled", null, default));
        Assert.Null(await billing.GetForCustomerAsync(UsageBilling.TenantId, "someone-else@example.com", "overage-settled", default));
        Assert.Equal(calls, provider.Requests.Count);
    }

    [Fact]
    public async Task Another_of_the_customers_own_cards_can_be_chosen_but_never_someone_elses()
    {
        var (name, provider, published, reference) = await PastDueAsync("overage-pay-6");
        Guid mine, theirs;
        await using (var db = NewDb(name))
        {
            var card = UsageBilling.AddCard(db, pm: "pm_mine_other", isDefault: false);
            mine = card.Id;
            theirs = UsageBilling.AddCard(db, pm: "pm_theirs", email: "other@example.com", customerId: "cus_other").Id;
            // AddCard gives each vault a random user; make the first one the signed-in customer's.
            var vault = await db.PaymentCustomers.SingleAsync(c => c.Id == card.PaymentCustomerId);
            db.Entry(vault).Property(c => c.UserId).CurrentValue = UserId;
            db.Entry(card).Property(c => c.UserId).CurrentValue = UserId;
            await db.SaveChangesAsync();
        }

        var calls = provider.Requests.Count;
        var refused = await Billing(name, provider, published, FirstFailure.AddHours(2)).PayAsync(UsageBilling.TenantId, Email, UserId, reference, theirs, default);
        Assert.Equal(UsageChargePayOutcome.UnknownPaymentMethod, refused!.Outcome);
        Assert.Equal(calls, provider.Requests.Count);

        var paid = await Billing(name, provider, published, FirstFailure.AddHours(3)).PayAsync(UsageBilling.TenantId, Email, UserId, reference, mine, default);
        Assert.Equal(UsageChargePayOutcome.Paid, paid!.Outcome);
        Assert.Equal(("pm_mine_other", true), (provider.Requests[^1].ProviderPaymentMethodId, provider.Requests[^1].CustomerPresent));
    }

    // ---- Helpers --------------------------------------------------------------------------------------

    /// <summary>A usage charge whose first attempt was declined (past due, one UsageChargeFailed), at an exclusive 8.25% store
    /// (pinned 1000 + 83 = 1083). <paramref name="outcomes"/> are answered in order, then Succeeded.</summary>
    private static async Task<(string Name, RecordingProvider Provider, List<object> Published, string Reference)> PastDueAsync(
        string reference, params PaymentOutcome[] outcomes)
    {
        var name = $"ucp_{Guid.NewGuid():N}";
        Guid store;
        await using (var db = NewDb(name))
        {
            store = AddStore(db);
            UsageBilling.AddCard(db); // auto top-ups need a default card (jobmgr_3_settlement)
        }

        var provider = new RecordingProvider();
        foreach (var outcome in outcomes.Length == 0 ? [PaymentOutcome.Failed] : outcomes)
        {
            provider.Outcomes.Enqueue(outcome);
        }

        var published = new List<object>();
        var kind = reference.StartsWith("autoload", StringComparison.Ordinal) ? UsageChargeKind.AutoLoad : UsageChargeKind.Overage;
        await Billing(name, provider, published, FirstFailure).ChargeAsync(UsageBilling.Request(store, 1000, reference, kind), default);
        Assert.IsType<UsageChargeFailed>(Assert.Single(published));
        return (name, provider, published, reference);
    }

    private static PaymentsDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseInMemoryDatabase(name).Options);

    private static Infrastructure.Consumers.UsageChargeBilling Billing(string name, RecordingProvider provider, List<object> published, DateTimeOffset at) =>
        Billing(NewDb(name), provider, published, at);

    private static Infrastructure.Consumers.UsageChargeBilling Billing(PaymentsDbContext db, RecordingProvider provider, List<object> published, DateTimeOffset at) =>
        UsageBilling.For(db, provider, new FixedTime(at), new RecordingPublisher { Published = published });

    private static Guid AddStore(PaymentsDbContext db)
    {
        var id = Guid.CreateVersion7();
        db.StorefrontLedgerAccounts.Add(new StorefrontLedgerAccounts
        {
            StorefrontId = id,
            ReceivableAccountCode = $"receivable.store-{id:N}",
            RevenueAccountCode = $"revenue.store-{id:N}",
            TaxAccountCode = $"tax.store-{id:N}",
            ShippingAccountCode = $"shipping.store-{id:N}",
            TaxRateBasisPoints = 825,
            TaxInclusive = false,
            UpdatedAt = FirstFailure,
        });
        db.SaveChanges();
        return id;
    }
}
