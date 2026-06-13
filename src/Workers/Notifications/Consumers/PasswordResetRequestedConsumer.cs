using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Identity;
using ThreeCommerce.Workers.Notifications.Email;

namespace ThreeCommerce.Workers.Notifications.Consumers;

public sealed class PasswordResetRequestedConsumer(IEmailSender sender, EmailTemplates templates) : IConsumer<PasswordResetRequested>
{
    public Task Consume(ConsumeContext<PasswordResetRequested> context) =>
        sender.SendAsync(
            templates.PasswordReset(context.Message.Email, context.Message.ResetToken),
            context.CancellationToken);
}
