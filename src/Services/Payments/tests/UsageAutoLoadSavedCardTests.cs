using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// jobmgr_3_settlement (owner decision 2026-11-06): a usage auto top-up charges the customer's DEFAULT saved card
/// off-session — the instrument machinery renewals use (pay_disp_5) — and with no default card it is SKIPPED: no provider
/// call, no pinned amount, no attempt, no posting, no dunning, and <see cref="UsageAutoLoadSkipped"/> announced once (Usage
/// takes the credit back, Notifications asks for a card). An overage, already consumed and owed, is still charged without
/// a card. A retry under a fresh key charges the card that is the default by then; an unanswered one keeps its card.
/// </summary>
public class UsageAutoLoadSavedCardTests
{
    // A sub-microsecond tick, so StoredTime truncation is exercised on a Mac too (engineering-gotchas).
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero).AddTicks(1_755_249);

    [Fact]
    public async Task An_auto_load_charges_the_customers_default_saved_card_off_session()
    {
        var name = $"uasc_{Guid.NewGuid():N}";
        await using var db = NewDb(name);
        UsageBilling.AddCard(db, pm: "pm_other", isDefault: false);
        UsageBilling.AddCard(db, pm: "pm_default");
        var provider = new RecordingProvider();
        var published = new RecordingPublisher();

        await UsageBilling.For(db, provider, new FixedTime(Now), published)
            .ChargeAsync(UsageBilling.Request(null, 1000, "autoload-uasc-1", UsageChargeKind.AutoLoad), default);

        var request = Assert.Single(provider.Requests);
        Assert.Equal(("cus_usage", "pm_default", false), (request.ProviderCustomerId, request.ProviderPaymentMethodId, request.CustomerPresent));
        Assert.IsType<UsageChargeSettled>(Assert.Single(published.Published));
        await using var fresh = NewDb(name);
        var charge = await fresh.UsageCharges.SingleAsync();
        Assert.Equal((UsageChargeStatus.Settled, "pm_default"), (charge.Status, charge.ProviderPaymentMethodId));
    }

    public static TheoryData<string> NoDefaultCard => new() { "no vault", "only a non-default card", "default card removed", "another customer's card", "vault email unknown" };

    [Theory]
    [MemberData(nameof(NoDefaultCard))]
    public async Task An_auto_load_with_no_default_card_is_skipped_never_attempted_and_announced_once(string situation)
    {
        var name = $"uasc_{Guid.NewGuid():N}";
        await using var db = NewDb(name);
        Arrange(db, situation);
        var provider = new RecordingProvider();
        var published = new RecordingPublisher();
        const string reference = "autoload-uasc-2";

        for (var delivery = 0; delivery < 2; delivery++) // a redelivered request skips nothing twice
        {
            await using var scope = NewDb(name);
            await UsageBilling.For(scope, provider, new FixedTime(Now), published)
                .ChargeAsync(UsageBilling.Request(null, 1000, reference, UsageChargeKind.AutoLoad, quantity: 100), default);
        }

        Assert.Empty(provider.Requests);
        var skipped = Assert.IsType<UsageAutoLoadSkipped>(Assert.Single(published.Published));
        Assert.Equal(
            (reference, 100L, 1000L, "USD", UsageAutoLoadSkipReason.NoSavedCard, "usage@example.com", Now.AddTicks(-9)),
            (skipped.Reference, skipped.Quantity, skipped.AmountMinor, skipped.Currency, skipped.Reason, skipped.CustomerEmail, skipped.SkippedAt));
        await using var fresh = NewDb(name);
        Assert.Equal(UsageChargeStatus.Skipped, (await fresh.UsageCharges.SingleAsync()).Status);
        Assert.Empty(await fresh.UsageChargeAttempts.ToListAsync());
        Assert.Empty(await fresh.IdempotencyRecords.ToListAsync());             // no amount pinned
        Assert.Empty(await fresh.JournalEntries.ToListAsync());
        Assert.Equal(0, await UsageBilling.For(fresh, provider, new FixedTime(Now.AddDays(30))).RetryDueAsync(default)); // never dunned
    }

    [Fact]
    public async Task An_overage_with_no_card_is_still_charged_as_before()
    {
        await using var db = NewDb($"uasc_{Guid.NewGuid():N}");
        var provider = new RecordingProvider();
        var published = new RecordingPublisher();

        await UsageBilling.For(db, provider, new FixedTime(Now), published).ChargeAsync(UsageBilling.Request(null, 900, "overage-uasc-3"), default);

        var request = Assert.Single(provider.Requests);
        Assert.Equal((null, null), (request.ProviderCustomerId, request.ProviderPaymentMethodId)); // the host account, as before
        Assert.IsType<UsageChargeSettled>(Assert.Single(published.Published));
    }

    [Fact]
    public async Task A_retry_under_a_fresh_key_charges_the_card_that_is_default_now()
    {
        var name = $"uasc_{Guid.NewGuid():N}";
        await using (var db = NewDb(name))
        {
            UsageBilling.AddCard(db, pm: "pm_declining");
        }

        var provider = new RecordingProvider();
        provider.Outcomes.Enqueue(PaymentOutcome.Failed);
        const string reference = "autoload-uasc-4";
        await UsageBilling.For(NewDb(name), provider, new FixedTime(Now)).ChargeAsync(UsageBilling.Request(null, 1000, reference, UsageChargeKind.AutoLoad), default);
        await using (var db = NewDb(name))
        {
            UsageBilling.AddCard(db, pm: "pm_replacement"); // the customer replaced the declining card
        }

        await UsageBilling.For(NewDb(name), provider, new FixedTime(Now.AddDays(1).AddHours(1))).RetryDueAsync(default);

        Assert.Equal([(reference, "pm_declining"), ($"{reference}-a1", "pm_replacement")], provider.Requests.Select(r => (r.IdempotencyKey, r.ProviderPaymentMethodId)));
        Assert.All(provider.Requests, r => Assert.False(r.CustomerPresent));
    }

    [Fact]
    public async Task An_unanswered_attempt_is_retried_under_its_key_with_the_card_it_was_sent_with()
    {
        var name = $"uasc_{Guid.NewGuid():N}";
        await using (var db = NewDb(name))
        {
            UsageBilling.AddCard(db, pm: "pm_first");
        }

        var provider = new RecordingProvider { ThrowFirst = 1 };
        const string reference = "autoload-uasc-5";
        await UsageBilling.For(NewDb(name), provider, new FixedTime(Now)).ChargeAsync(UsageBilling.Request(null, 1000, reference, UsageChargeKind.AutoLoad), default);
        await using (var db = NewDb(name))
        {
            UsageBilling.AddCard(db, pm: "pm_second");
        }

        await UsageBilling.For(NewDb(name), provider, new FixedTime(Now.AddDays(1).AddHours(1))).RetryDueAsync(default);

        // Same key, same parameters: a provider rejects a reused key sent with another card.
        Assert.Equal([(reference, "pm_first"), (reference, "pm_first")], provider.Requests.Select(r => (r.IdempotencyKey, r.ProviderPaymentMethodId)));
    }

    [Fact]
    public void A_vault_records_its_customers_email_normalized_and_reports_a_change()
    {
        var customer = new PaymentCustomer
        {
            Id = Guid.CreateVersion7(), TenantId = UsageBilling.TenantId, UserId = Guid.CreateVersion7(), Provider = "stripe",
            ProviderCustomerId = "cus_x", CreatedAt = Now,
        };

        Assert.True(customer.RecordEmail("  Usage@Example.COM "));
        Assert.Equal("usage@example.com", customer.Email);
        Assert.False(customer.RecordEmail("usage@example.com"));
        Assert.False(customer.RecordEmail("   "));
        Assert.True(customer.RecordEmail("new@example.com"));
    }

    [Fact]
    public void Only_an_unattempted_auto_load_can_be_skipped()
    {
        var overage = UsageCharge.Record(UsageBilling.TenantId, null, "u@example.com", UsageChargeKind.Overage, BuildingBlocks.Contracts.Supply.MeterType.Token,
            1, 0, 10, "USD", "overage-x", "d", Now);
        Assert.Throws<InvalidOperationException>(() => overage.Skip(Now));

        var attempted = UsageCharge.Record(UsageBilling.TenantId, null, "u@example.com", UsageChargeKind.AutoLoad, BuildingBlocks.Contracts.Supply.MeterType.Token,
            1, 0, 10, "USD", "autoload-x", "d", Now);
        attempted.KeyIssued(Now);
        Assert.Throws<InvalidOperationException>(() => attempted.Skip(Now));
    }

    private static void Arrange(PaymentsDbContext db, string situation)
    {
        switch (situation)
        {
            case "no vault":
                break;
            case "only a non-default card":
                UsageBilling.AddCard(db, isDefault: false);
                break;
            case "default card removed":
                UsageBilling.AddCard(db).Remove(Now);
                db.SaveChanges();
                break;
            case "another customer's card":
                UsageBilling.AddCard(db, email: "someone-else@example.com", customerId: "cus_other");
                break;
            case "vault email unknown":
                var card = UsageBilling.AddCard(db, customerId: "cus_legacy");
                var legacy = db.PaymentCustomers.Single(c => c.Id == card.PaymentCustomerId);
                db.Entry(legacy).Property(c => c.Email).CurrentValue = null; // a vault from before the email was kept
                db.SaveChanges();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(situation));
        }
    }

    private static PaymentsDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseInMemoryDatabase(name).Options);
}
