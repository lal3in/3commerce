namespace ThreeCommerce.BuildingBlocks.Infrastructure.Notifications;

public enum NotificationChannel { Email = 1, Sms = 2, Push = 3 }

public enum NotificationCategory { Transactional = 1, Security = 2, Marketing = 3 }

public enum NotificationSeverity { Info = 1, Warning = 2, Critical = 3 }

/// <summary>A channel-agnostic notification (mt6_5). Email-first; SMS/push slot in behind the channel seam.</summary>
public sealed record Notification(
    string Recipient,
    NotificationCategory Category,
    NotificationSeverity Severity,
    string Subject,
    string Body,
    NotificationChannel Channel = NotificationChannel.Email);

/// <summary>One delivery sink (mt6_5). Email wraps the existing IEmailSender; others are future adapters.</summary>
public interface INotificationChannel
{
    public NotificationChannel Channel { get; }

    public Task SendAsync(Notification notification, CancellationToken ct);
}

/// <summary>A recipient's notification preferences (mt6_5). Security alerts are deliberately not opt-out-able.</summary>
public sealed record NotificationPreferences(bool MarketingOptIn = false, bool TransactionalMuted = false);

/// <summary>
/// Decides whether a notification is delivered (mt6_5): security/high-risk alerts ALWAYS go out (they
/// cannot be muted), marketing requires explicit opt-in, and transactional is on by default unless muted.
/// </summary>
public static class NotificationPolicy
{
    public static bool ShouldDeliver(Notification notification, NotificationPreferences preferences) => notification.Category switch
    {
        NotificationCategory.Security => true,
        NotificationCategory.Marketing => preferences.MarketingOptIn,
        _ => !preferences.TransactionalMuted,
    };
}

/// <summary>
/// Builds a high-risk security alert from an audited event (mt6_5), pairing with the mt6_2 denied-attempt
/// audit. Content is deliberately minimal (GOTCHA): the action + a resource reference + outcome, never
/// the sensitive payload — an operator follows the reference into the audit log for detail.
/// </summary>
public static class SecurityAlert
{
    public static Notification ForAuditEvent(string recipient, string action, string resourceType, string resourceId, string outcome)
    {
        var severity = outcome.Equals("Denied", StringComparison.OrdinalIgnoreCase)
            ? NotificationSeverity.Warning
            : NotificationSeverity.Critical;

        return new Notification(
            recipient,
            NotificationCategory.Security,
            severity,
            Subject: $"Security alert: {action} ({outcome})",
            Body: $"A high-risk action was recorded.\nAction: {action}\nResource: {resourceType}/{resourceId}\nOutcome: {outcome}\nSee the audit log for details.");
    }
}
