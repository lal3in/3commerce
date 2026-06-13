using Microsoft.Extensions.Logging;

namespace ThreeCommerce.Workers.Notifications.Email;

/// <summary>
/// Dev/sandbox sender: writes the full email (including action links) to the log so
/// flows are testable without an email provider. NOT for any deployed environment.
/// </summary>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        logger.LogInformation(
            "EMAIL → {To}\n  Subject: {Subject}\n  {Body}",
            message.To, message.Subject, message.Body);
        return Task.CompletedTask;
    }
}
