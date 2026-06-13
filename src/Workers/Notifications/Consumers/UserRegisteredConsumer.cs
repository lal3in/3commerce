using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Identity;
using ThreeCommerce.Workers.Notifications.Email;

namespace ThreeCommerce.Workers.Notifications.Consumers;

public sealed class UserRegisteredConsumer(IEmailSender sender, EmailTemplates templates) : IConsumer<UserRegistered>
{
    public Task Consume(ConsumeContext<UserRegistered> context) =>
        sender.SendAsync(
            templates.Verification(context.Message.Email, context.Message.VerificationToken),
            context.CancellationToken);
}
