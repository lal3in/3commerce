namespace ThreeCommerce.Workers.Notifications.Domain;

public enum NotificationStatus
{
    Sent = 1,
    Failed = 2,
}

/// <summary>
/// A record of one attempted notification send (mc_proc_4). The Notifications worker owns this read
/// model so operators can see the delivery pipeline (sent / failed) in Mission Control — a payment or
/// order email that fails to go out is visible instead of silently lost.
/// </summary>
public sealed class NotificationDelivery
{
    public Guid Id { get; init; }
    public string Channel { get; init; } = "email";
    public required string Recipient { get; init; }
    public required string Subject { get; init; }
    public NotificationStatus Status { get; set; }

    /// <summary>
    /// What the email was about (<c>EmailMessage.Reference</c>, e.g. <c>order-confirmed:{orderId}</c>). Null on rows
    /// written before the column existed and for emails without a subject entity.
    /// </summary>
    public string? Reference { get; init; }
    public string? Error { get; set; }
    public DateTimeOffset OccurredAt { get; init; }
}
