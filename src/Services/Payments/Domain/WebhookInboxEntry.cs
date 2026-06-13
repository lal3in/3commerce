namespace ThreeCommerce.Payments.Domain;

/// <summary>Dedup key for provider webhook events — guarantees a webhook is processed once.</summary>
public class WebhookInboxEntry
{
    public required string EventId { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }
}
