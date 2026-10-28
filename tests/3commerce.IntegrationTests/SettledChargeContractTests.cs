using System.Text.Json;
using MassTransit.Serialization;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.BuildingBlocks.Contracts.Tax.Facts;
using ThreeCommerce.Workers.Notifications.Consumers;
using ThreeCommerce.Workers.Notifications.Email;
using OrderingTax = ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Unit lane — the contracts of ADR-0063 P4b (<c>tax_p4b_settled_charge_events</c>) and the usage-charge failure feedback
/// (<c>usage_charge_failure_feedback</c>): the new events round-trip through MassTransit's serializer with their enums as
/// NUMBERS; a usage charge request published before <c>UnitPriceMinor</c> was appended still deserializes (0); the
/// published tax-fact vocabulary has Ordering's P1 values exactly; and the declined / abandoned usage-charge emails say
/// what happened, when it is retried and when billable usage is suspended.
/// </summary>
public sealed class SettledChargeContractTests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset At = new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void A_usage_charge_request_published_before_the_unit_price_still_deserializes()
    {
        const string preP4b = """
            {"tenantId":"00000000-0000-0000-0000-000000000001","customerEmail":"m@example.com","meter":3,"overageQuantity":30,
             "chargeMinor":600,"currency":"USD","reference":"overage-x-30","storefrontId":null}
            """;
        var overage = JsonSerializer.Deserialize<UsageOverageCharge>(preP4b, SystemTextJsonMessageSerializer.Options)!;
        Assert.Equal((30L, 600L, 0L), (overage.OverageQuantity, overage.ChargeMinor, overage.UnitPriceMinor));

        var autoLoad = JsonSerializer.Deserialize<UsageAutoLoadCharge>(
            preP4b.Replace("overageQuantity", "reloadQuantity", StringComparison.Ordinal), SystemTextJsonMessageSerializer.Options)!;
        Assert.Equal((30L, 0L), (autoLoad.ReloadQuantity, autoLoad.UnitPriceMinor));
    }

    [Fact]
    public void The_settled_events_round_trip_with_their_tax_facts_as_numbers()
    {
        var line = new SettledChargeLine("Subscription renewal", 2, 2000, TaxCategory.Standard, 825, 2000, 165);
        var renewal = new SubscriptionRenewalSettled(
            TenantId, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "s@example.com", Guid.CreateVersion7(), null, 2,
            At, At.AddMonths(1), "renew-x-2", "USD", 2000, 165, 2165, false, TaxScheme.Domestic, [line], At, "pi_1");
        var usage = new UsageChargeSettled(
            TenantId, null, "m@example.com", UsageChargeKind.AutoLoad, MeterType.Request, 50, 22, "autoload-x-1", "AUD", 1000, 100, 1100,
            true, TaxScheme.Domestic, [line with { TaxCategory = TaxCategory.Exempt, ExemptionReason = TaxExemptionReason.DestinationShipRule }], At, 1);

        var renewalJson = JsonSerializer.Serialize(renewal, SystemTextJsonMessageSerializer.Options);
        var usageJson = JsonSerializer.Serialize(usage, SystemTextJsonMessageSerializer.Options);

        Assert.Contains("\"taxScheme\":1", renewalJson, StringComparison.Ordinal);
        Assert.Contains("\"taxCategory\":1", renewalJson, StringComparison.Ordinal);
        Assert.Contains("\"kind\":2", usageJson, StringComparison.Ordinal);
        Assert.Contains("\"exemptionReason\":1", usageJson, StringComparison.Ordinal);
        var renewalBack = JsonSerializer.Deserialize<SubscriptionRenewalSettled>(renewalJson, SystemTextJsonMessageSerializer.Options)!;
        var usageBack = JsonSerializer.Deserialize<UsageChargeSettled>(usageJson, SystemTextJsonMessageSerializer.Options)!;
        Assert.Equal(renewal with { Lines = [] }, renewalBack with { Lines = [] });
        Assert.Equal(line, Assert.Single(renewalBack.Lines));
        Assert.Equal(usage with { Lines = [] }, usageBack with { Lines = [] });
        Assert.Equal(TaxExemptionReason.DestinationShipRule, Assert.Single(usageBack.Lines).ExemptionReason);
    }

    [Fact]
    public void The_failure_event_round_trips_with_its_reason_as_a_number()
    {
        var failed = Failed(ChargeFailureReason.RequiresAction, final: false);
        var json = JsonSerializer.Serialize(failed, SystemTextJsonMessageSerializer.Options);

        Assert.Contains("\"reason\":2", json, StringComparison.Ordinal);
        Assert.Equal(failed, JsonSerializer.Deserialize<UsageChargeFailed>(json, SystemTextJsonMessageSerializer.Options));
    }

    [Fact]
    public void The_published_tax_fact_vocabulary_matches_orderings_values()
    {
        AssertSameMembers<TaxCategory, OrderingTax.TaxCategory>();
        AssertSameMembers<TaxExemptionReason, OrderingTax.TaxExemptionReason>();
        AssertSameMembers<TaxScheme, OrderingTax.TaxScheme>();
    }

    [Fact]
    public void A_declined_auto_load_email_says_the_credit_is_held_and_when_it_is_retried()
    {
        var templates = new EmailTemplates("https://shop.example");
        var email = templates.UsageChargeFailed(Failed(ChargeFailureReason.Failed, final: false));

        Assert.Equal("m@example.com", email.To);
        Assert.Contains("didn't go through", email.Subject, StringComparison.Ordinal);
        Assert.Contains("21.65 USD", email.Body, StringComparison.Ordinal);
        Assert.Contains("top-up", email.Body, StringComparison.Ordinal);
        Assert.Contains("on hold", email.Body, StringComparison.Ordinal);
        Assert.Contains("try again on 29 October 2026", email.Body, StringComparison.Ordinal);
        Assert.Contains("unpaid after 4 November 2026", email.Body, StringComparison.Ordinal);
        Assert.Contains("https://shop.example/account", email.Body, StringComparison.Ordinal);
        Assert.Equal("usage-charge-failed:autoload-x-1:20261029T093000Z", email.Reference);

        var asked = templates.UsageChargeFailed(Failed(ChargeFailureReason.RequiresAction, final: false) with { Kind = UsageChargeKind.Overage });
        Assert.Contains("bank asked to confirm", asked.Body, StringComparison.Ordinal);
        Assert.Contains("still due", asked.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_final_failure_email_says_billable_usage_is_suspended_and_the_consumer_sends_it()
    {
        var templates = new EmailTemplates("https://shop.example");
        var final = Failed(ChargeFailureReason.Failed, final: true);
        var sender = new CollectingSender();

        await new UsageChargeFailedEmailConsumer(sender, templates).Consume(SubscriptionRenewalActionEmailTests.StubContext<UsageChargeFailed>.For(final));

        var email = Assert.Single(sender.Sent);
        Assert.Contains("suspended", email.Subject, StringComparison.Ordinal);
        Assert.Contains("allowance", email.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("try again", email.Body, StringComparison.Ordinal);
        Assert.Equal("usage-charge-failed:autoload-x-1:final", email.Reference);
    }

    private static UsageChargeFailed Failed(ChargeFailureReason reason, bool final) =>
        new(TenantId, null, "m@example.com", UsageChargeKind.AutoLoad, MeterType.Request, 100, "autoload-x-1", 2_165, "USD", reason,
            final ? 3 : 0, At, final ? null : At.AddDays(1), At.AddDays(7), final);

    private static void AssertSameMembers<TContract, TOrdering>()
        where TContract : struct, Enum
        where TOrdering : struct, Enum
    {
        var contract = Enum.GetValues<TContract>().ToDictionary(v => v.ToString(), v => Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture));
        var ordering = Enum.GetValues<TOrdering>().ToDictionary(v => v.ToString(), v => Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(ordering.OrderBy(kv => kv.Value), contract.OrderBy(kv => kv.Value));
    }

    private sealed class CollectingSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }
}
