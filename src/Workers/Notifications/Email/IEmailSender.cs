namespace ThreeCommerce.Workers.Notifications.Email;

/// <summary>
/// Transactional email seam. v1 has a single dev/sandbox implementation that logs;
/// a real provider (SMTP/API) slots in behind this without touching consumers.
/// Provider choice is deferred (PRD §8) — never couple templates to a provider.
/// </summary>
public interface IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct);
}

/// <summary>
/// One outgoing email. <paramref name="Reference"/> names WHAT the email is about (e.g.
/// <c>order-confirmed:{orderId}</c>, see <see cref="NotificationReferences"/>) and is stored on the delivery log, so
/// "did this order get its confirmation?" has an exact answer (ADR-0060 backfill). Optional and never rendered.
/// </summary>
public record EmailMessage(string To, string Subject, string Body, string? Reference = null);

/// <summary>The delivery-log reference keys (<see cref="EmailMessage.Reference"/>). Stable: operator tooling matches on them.</summary>
public static class NotificationReferences
{
    public const string OrderConfirmedPrefix = "order-confirmed:";

    public static string OrderConfirmed(Guid orderId) => OrderConfirmedPrefix + orderId.ToString("D");
}
