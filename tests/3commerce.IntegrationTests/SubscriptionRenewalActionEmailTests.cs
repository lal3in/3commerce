using System.Reflection;
using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Workers.Notifications.Consumers;
using ThreeCommerce.Workers.Notifications.Email;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// A subscription renewal that needs the customer to authenticate (RequiresAction) emails them (owner decision
/// 2026-10-27, ADR-0057 amendment): to the subscriber, naming the amount the renewal asked for, that nothing was charged,
/// and where to go — and the delivery-log reference names the subscription period. Unit lane: no Docker.
/// </summary>
public sealed class SubscriptionRenewalActionEmailTests
{
    private static readonly EmailTemplates Templates = new("https://shop.example");

    private static readonly SubscriptionRenewalActionRequired Event = new(
        Guid.Parse("00000000-0000-0000-0000-000000000001"), Guid.Parse("019f74dc-5bc1-7135-9dbd-176f10ba4684"), Guid.CreateVersion7(),
        "subscriber@example.com", 2_165, "USD", new DateTimeOffset(2026, 11, 27, 9, 30, 0, TimeSpan.Zero));

    [Fact]
    public void The_email_asks_the_subscriber_to_confirm_the_renewal_amount_and_says_nothing_was_charged()
    {
        var email = Templates.SubscriptionRenewalActionRequired(Event);

        Assert.Equal("subscriber@example.com", email.To);
        Assert.Contains("confirm", email.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("21.65 USD", email.Body, StringComparison.Ordinal);
        Assert.Contains("has not been charged", email.Body, StringComparison.Ordinal);
        Assert.Contains("https://shop.example/account", email.Body, StringComparison.Ordinal);
        Assert.Equal(
            "subscription-renewal-action:019f74dc-5bc1-7135-9dbd-176f10ba4684:20261127T093000Z",
            email.Reference);
    }

    [Fact]
    public async Task The_consumer_sends_that_email()
    {
        var sender = new CollectingSender();
        var consumer = new SubscriptionRenewalActionRequiredConsumer(sender, Templates);

        await consumer.Consume(ConsumeContextStub.For(Event));

        var sent = Assert.Single(sender.Sent);
        Assert.Equal(Templates.SubscriptionRenewalActionRequired(Event), sent);
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

    /// <summary>A consume context answering only what the consumer reads (<c>Message</c>, <c>CancellationToken</c>).</summary>
    public class ConsumeContextStub : DispatchProxy
    {
        private SubscriptionRenewalActionRequired? _message;

        public static ConsumeContext<SubscriptionRenewalActionRequired> For(SubscriptionRenewalActionRequired message)
        {
            var context = Create<ConsumeContext<SubscriptionRenewalActionRequired>, ConsumeContextStub>();
            ((ConsumeContextStub)(object)context)._message = message;
            return context;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_Message" => _message,
            "get_CancellationToken" => CancellationToken.None,
            _ => throw new NotSupportedException(targetMethod?.Name),
        };
    }
}
