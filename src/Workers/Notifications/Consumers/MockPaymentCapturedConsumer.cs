using MassTransit;
using Microsoft.Extensions.Configuration;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Workers.Notifications.Email;

namespace ThreeCommerce.Workers.Notifications.Consumers;

/// <summary>
/// Renders the TEST-ONLY mock-payment payload email (pay_3, ADR-0039). The event is only ever
/// published in LocalMock/Sandbox (Production refuses the mock/email path at boot), so consuming it
/// is safe by construction. The recipient is the ops address <c>Payments:MockEmailTo</c>.
/// </summary>
public sealed class MockPaymentCapturedConsumer(IEmailSender sender, EmailTemplates templates, IConfiguration configuration)
    : IConsumer<MockPaymentCaptured>
{
    public Task Consume(ConsumeContext<MockPaymentCaptured> context)
    {
        var to = configuration["Payments:MockEmailTo"] is { Length: > 0 } configured
            ? configured
            : "dev-payments@localhost";
        return sender.SendAsync(templates.MockPayment(to, context.Message), context.CancellationToken);
    }
}
