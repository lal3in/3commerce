using System.Text.Json;
using MassTransit.Serialization;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Workers.Notifications.Consumers;
using ThreeCommerce.Workers.Notifications.Email;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Unit lane — jobmgr_3_settlement (owner decision 2026-11-06): the skipped-top-up and card-ready events round-trip through
/// MassTransit's serializer with their enums as NUMBERS, and the skipped top-up's email tells the customer nothing was
/// charged or credited and where to add a card (the account's saved cards), keyed once per top-up in the delivery log.
/// </summary>
public sealed class UsageAutoLoadSkippedContractTests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset At = new(2026, 11, 6, 9, 30, 0, TimeSpan.Zero);

    private static UsageAutoLoadSkipped Skipped() =>
        new(TenantId, null, "m@example.com", MeterType.Request, 100, "autoload-x-3", 2_000, "USD", UsageAutoLoadSkipReason.NoSavedCard, At);

    [Fact]
    public void The_events_round_trip_with_their_enums_as_numbers()
    {
        var skipped = Skipped();
        var json = Compact(JsonSerializer.Serialize(skipped, SystemTextJsonMessageSerializer.Options));
        Assert.Contains("\"reason\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"meter\":", json, StringComparison.Ordinal);
        Assert.Equal(skipped, JsonSerializer.Deserialize<UsageAutoLoadSkipped>(json, SystemTextJsonMessageSerializer.Options));

        var ready = new CustomerPaymentMethodReady(TenantId, "m@example.com", At);
        var readyJson = JsonSerializer.Serialize(ready, SystemTextJsonMessageSerializer.Options);
        Assert.DoesNotContain("pm_", readyJson, StringComparison.Ordinal); // no card details cross
        Assert.Equal(ready, JsonSerializer.Deserialize<CustomerPaymentMethodReady>(readyJson, SystemTextJsonMessageSerializer.Options));
    }

    [Fact]
    public async Task The_skipped_top_up_email_asks_for_a_card_and_says_nothing_was_charged()
    {
        var templates = new EmailTemplates("https://shop.example");
        var sender = new CollectingSender();

        await new UsageAutoLoadSkippedEmailConsumer(sender, templates)
            .Consume(SubscriptionRenewalActionEmailTests.StubContext<UsageAutoLoadSkipped>.For(Skipped()));

        var email = Assert.Single(sender.Sent);
        Assert.Equal("m@example.com", email.To);
        Assert.Contains("Add a card", email.Subject, StringComparison.Ordinal);
        Assert.Contains("Nothing was charged and no credit was added", email.Body, StringComparison.Ordinal);
        Assert.Contains("20.00 USD", email.Body, StringComparison.Ordinal);
        Assert.Contains("100 units", email.Body, StringComparison.Ordinal);
        Assert.Contains("https://shop.example/account", email.Body, StringComparison.Ordinal);
        Assert.Equal("usage-autoload-skipped:autoload-x-3", email.Reference);
    }

    private static string Compact(string json) =>
        JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement, new JsonSerializerOptions { WriteIndented = false });

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
