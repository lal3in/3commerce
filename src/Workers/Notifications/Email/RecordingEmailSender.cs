using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.Workers.Notifications.Domain;
using ThreeCommerce.Workers.Notifications.Infrastructure;

namespace ThreeCommerce.Workers.Notifications.Email;

/// <summary>
/// Wraps the real <see cref="IEmailSender"/> and records every attempt into the delivery log
/// (mc_proc_4). Recording is best-effort in its own DB scope and NEVER changes send behaviour — a
/// provider failure is recorded as Failed and re-thrown so MassTransit retries as before.
/// </summary>
public sealed class RecordingEmailSender(
    IEmailSender inner,
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<RecordingEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        try
        {
            await inner.SendAsync(message, ct);
            await RecordAsync(message, NotificationStatus.Sent, null);
        }
        catch (Exception ex)
        {
            await RecordAsync(message, NotificationStatus.Failed, ex.Message);
            throw;
        }
    }

    private async Task RecordAsync(EmailMessage message, NotificationStatus status, string? error)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            db.Deliveries.Add(new NotificationDelivery
            {
                Id = Guid.CreateVersion7(),
                Channel = "email",
                Recipient = message.To,
                Subject = message.Subject,
                Status = status,
                Error = Trim(error, 1000),
                OccurredAt = clock.GetUtcNow(),
            });
            // Deliberately NOT tied to the send's CancellationToken — a cancelled send must still be logged.
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to record notification delivery (subject {Subject})", message.Subject);
        }
    }

    private static string? Trim(string? value, int max) =>
        value is { Length: > 0 } && value.Length > max ? value[..max] : value;
}
