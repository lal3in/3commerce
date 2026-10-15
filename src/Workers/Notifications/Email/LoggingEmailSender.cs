using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ThreeCommerce.Workers.Notifications.Email;

/// <summary>
/// Dev/sandbox sender: no email leaves the process, the email is written to the log instead.
/// <para>
/// <b>Development only</b> logs the FULL email — recipient, subject and body, including the verification and
/// password-reset links — so local flows (and <c>scripts/e2e-verify.sh --live</c>, which reads those links from
/// <c>.run/notifications.log</c>) work without an email provider. Those links are bearer secrets.
/// </para>
/// <para>
/// <b>Every other environment</b> logs only non-secret metadata: subject, delivery reference and a short
/// SHA-256 hash of the recipient — never the body, a link, a token or the address. A non-Development host
/// only starts with this sender at all when <see cref="EmailSenderGuard"/> sees the sandbox acknowledgement.
/// </para>
/// </summary>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger, IHostEnvironment environment) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        if (environment.IsDevelopment())
        {
            logger.LogInformation(
                "EMAIL → {To}\n  Subject: {Subject}\n  {Body}",
                message.To, message.Subject, message.Body);
        }
        else
        {
            logger.LogInformation(
                "EMAIL (not delivered, body withheld) → recipient {RecipientHash}, subject {Subject}, reference {Reference}",
                RecipientHash(message.To), message.Subject, message.Reference ?? "-");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// A short, stable pseudonym for a recipient (first 12 hex chars of SHA-256 over the trimmed, lower-cased
    /// address): lets an operator correlate log lines for one recipient without the log holding the address.
    /// </summary>
    public static string RecipientHash(string address)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(address.Trim().ToLowerInvariant()));
        return "rcpt-" + Convert.ToHexStringLower(bytes)[..12];
    }
}
