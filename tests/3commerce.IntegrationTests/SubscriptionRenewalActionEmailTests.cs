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

    private static readonly DateTimeOffset NextRetry = new(2026, 11, 28, 9, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CancelsAfter = new(2026, 12, 4, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Dunning_emails_link_the_pay_page_and_say_when_it_is_retried_and_cancelled()
    {
        // Dunning (owner decision 2026-10-27): every email about an unpaid renewal links /account/subscriptions/{id}/pay.
        const string payLink = "https://shop.example/account/subscriptions/019f74dc-5bc1-7135-9dbd-176f10ba4684/pay";
        var asked = Templates.SubscriptionRenewalActionRequired(Event with { NextRetryAt = NextRetry, CancelsAfter = CancelsAfter });
        Assert.Contains(payLink, asked.Body, StringComparison.Ordinal);
        Assert.Contains("try again on 28 November 2026", asked.Body, StringComparison.Ordinal);
        Assert.Contains("unpaid after 4 December 2026", asked.Body, StringComparison.Ordinal);

        var failed = Templates.SubscriptionPaymentFailed(new SubscriptionPaymentFailed(
            Event.TenantId, Event.SubscriptionId, Event.StorefrontId, Event.CustomerEmail, 2_165, "USD", Event.PeriodEnd, NextRetry, CancelsAfter));
        Assert.Equal("subscriber@example.com", failed.To);
        Assert.Contains("didn't go through", failed.Subject, StringComparison.Ordinal);
        Assert.Contains("21.65 USD", failed.Body, StringComparison.Ordinal);
        Assert.Contains(payLink, failed.Body, StringComparison.Ordinal);
        Assert.Contains("try again on 28 November 2026", failed.Body, StringComparison.Ordinal);
        Assert.Equal(
            "subscription-payment-failed:019f74dc-5bc1-7135-9dbd-176f10ba4684:20261127T093000Z:20261128T093000Z",
            failed.Reference);
        var lastRetry = Templates.SubscriptionPaymentFailed(new SubscriptionPaymentFailed(
            Event.TenantId, Event.SubscriptionId, Event.StorefrontId, Event.CustomerEmail, 2_165, "USD", Event.PeriodEnd, null, CancelsAfter));
        Assert.DoesNotContain("try again on", lastRetry.Body, StringComparison.Ordinal);
        Assert.EndsWith(":final", lastRetry.Reference, StringComparison.Ordinal);

        var cancelled = Templates.SubscriptionCancelledForNonPayment(new SubscriptionCancelledForNonPayment(
            Event.TenantId, Event.SubscriptionId, Event.StorefrontId, Event.CustomerEmail, 2_165, "USD", Event.PeriodEnd));
        Assert.Contains("cancelled", cancelled.Subject, StringComparison.Ordinal);
        Assert.Contains("21.65 USD", cancelled.Body, StringComparison.Ordinal);
        Assert.Contains("not been charged", cancelled.Body, StringComparison.Ordinal);
        Assert.Equal(
            "subscription-cancelled-non-payment:019f74dc-5bc1-7135-9dbd-176f10ba4684:20261127T093000Z",
            cancelled.Reference);
    }

    [Fact]
    public async Task The_dunning_consumers_send_their_emails()
    {
        var sender = new CollectingSender();
        var failed = new SubscriptionPaymentFailed(
            Event.TenantId, Event.SubscriptionId, Event.StorefrontId, Event.CustomerEmail, 2_165, "USD", Event.PeriodEnd, NextRetry, CancelsAfter);
        var cancelled = new SubscriptionCancelledForNonPayment(
            Event.TenantId, Event.SubscriptionId, Event.StorefrontId, Event.CustomerEmail, 2_165, "USD", Event.PeriodEnd);

        await new SubscriptionPaymentFailedConsumer(sender, Templates).Consume(StubContext<SubscriptionPaymentFailed>.For(failed));
        await new SubscriptionCancelledForNonPaymentConsumer(sender, Templates).Consume(StubContext<SubscriptionCancelledForNonPayment>.For(cancelled));

        Assert.Equal([Templates.SubscriptionPaymentFailed(failed), Templates.SubscriptionCancelledForNonPayment(cancelled)], sender.Sent);
    }

    /// <summary>A consume context answering only what a consumer reads (<c>Message</c>, <c>CancellationToken</c>).</summary>
    public class StubContext<T> : DispatchProxy
        where T : class
    {
        private T? _message;

        public static ConsumeContext<T> For(T message)
        {
            var context = Create<ConsumeContext<T>, StubContext<T>>();
            ((StubContext<T>)(object)context)._message = message;
            return context;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_Message" => _message,
            "get_CancellationToken" => CancellationToken.None,
            _ => throw new NotSupportedException(targetMethod?.Name),
        };
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
