using System.Text.Json;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

public sealed class StreamOutboxMessage
{
    public Guid Id { get; private set; }
    public string Topic { get; private set; } = string.Empty;
    public string Key { get; private set; } = string.Empty;
    public string EventType { get; private set; } = string.Empty;
    public int EventVersion { get; private set; }
    public Guid? TenantId { get; private set; }
    public string PayloadJson { get; private set; } = string.Empty;
    public string HeadersJson { get; private set; } = "{}";
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset AvailableAt { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public int PublishAttempts { get; private set; }
    public string? LastError { get; private set; }

    private StreamOutboxMessage() { }

    public static StreamOutboxMessage Stage(
        string topic,
        string key,
        string eventType,
        int eventVersion,
        Guid? tenantId,
        string payloadJson,
        string headersJson,
        DateTimeOffset occurredAt,
        DateTimeOffset? availableAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        JsonDocument.Parse(payloadJson).Dispose();
        JsonDocument.Parse(headersJson).Dispose();
        if (eventVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(eventVersion), eventVersion, "Event version must be positive.");

        return new StreamOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            Topic = topic.Trim(),
            Key = key.Trim(),
            EventType = eventType.Trim(),
            EventVersion = eventVersion,
            TenantId = tenantId,
            PayloadJson = payloadJson,
            HeadersJson = headersJson,
            OccurredAt = occurredAt,
            AvailableAt = availableAt ?? occurredAt,
        };
    }

    public void MarkPublished(DateTimeOffset publishedAt)
    {
        PublishedAt = publishedAt;
        LastError = null;
    }

    public void MarkFailed(string error, DateTimeOffset nextAvailableAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        PublishAttempts++;
        LastError = error.Length > 2_000 ? error[..2_000] : error;
        AvailableAt = nextAvailableAt;
    }
}
