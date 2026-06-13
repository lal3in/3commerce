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

public record EmailMessage(string To, string Subject, string Body);
