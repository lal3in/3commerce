namespace ThreeCommerce.BuildingBlocks.Infrastructure.Webhooks;

/// <summary>A tenant's outbound webhook endpoint + which events it wants (mt6_6).</summary>
public sealed class WebhookSubscription
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public required string Url { get; init; }

    /// <summary>Per-subscription HMAC secret (the receiver verifies with it). Never logged.</summary>
    public required string Secret { get; init; }

    /// <summary>Comma-separated event types, or "*" for all (but not high-volume analytics — see GOTCHA).</summary>
    public required string EventTypes { get; init; }
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; init; }

    public bool HandlesEvent(string eventType) =>
        Active && EventTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(t => t == "*" || t.Equals(eventType, StringComparison.OrdinalIgnoreCase));
}

public enum WebhookDeliveryStatus { Pending = 1, Delivered = 2, Retrying = 3, Exhausted = 4 }

/// <summary>
/// Tracks the delivery of one event to one subscription (mt6_6): the attempt count, last result, and —
/// on failure — when to retry next (exponential backoff). After <see cref="MaxAttempts"/> it is
/// Exhausted and the operator can see why in the log. A scheduled sweep (mt6_3) redelivers due rows.
/// </summary>
public sealed class WebhookDelivery
{
    public const int MaxAttempts = 6;

    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid SubscriptionId { get; init; }
    public required string EventId { get; init; }
    public required string EventType { get; init; }
    public int Attempts { get; private set; }
    public WebhookDeliveryStatus Status { get; private set; } = WebhookDeliveryStatus.Pending;
    public int? LastStatusCode { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? NextAttemptAt { get; private set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? DeliveredAt { get; private set; }

    public bool IsDue(DateTimeOffset now) =>
        Status is WebhookDeliveryStatus.Pending or WebhookDeliveryStatus.Retrying && NextAttemptAt <= now;

    private WebhookDelivery() { }

    public static WebhookDelivery Queue(Guid tenantId, Guid subscriptionId, string eventId, string eventType, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            SubscriptionId = subscriptionId,
            EventId = eventId,
            EventType = eventType,
            CreatedAt = now,
            NextAttemptAt = now,
        };

    public void RecordSuccess(int statusCode, DateTimeOffset now)
    {
        Attempts++;
        Status = WebhookDeliveryStatus.Delivered;
        LastStatusCode = statusCode;
        LastError = null;
        NextAttemptAt = null;
        DeliveredAt = now;
    }

    public void RecordFailure(int? statusCode, string error, DateTimeOffset now)
    {
        Attempts++;
        LastStatusCode = statusCode;
        LastError = error.Length > 500 ? error[..500] : error;

        if (Attempts >= MaxAttempts)
        {
            Status = WebhookDeliveryStatus.Exhausted;
            NextAttemptAt = null;
        }
        else
        {
            Status = WebhookDeliveryStatus.Retrying;
            NextAttemptAt = now + Backoff(Attempts);
        }
    }

    /// <summary>Exponential backoff capped at an hour: ~2, 4, 8, 16, 32 min then 60.</summary>
    public static TimeSpan Backoff(int attempt)
    {
        var minutes = Math.Min(60, Math.Pow(2, attempt));
        return TimeSpan.FromMinutes(minutes);
    }
}
