namespace ThreeCommerce.BuildingBlocks.Contracts.Streams;

/// <summary>
/// Versioned Kafka event-stream envelope (ADR-0034). The payload is a committed fact from
/// the owning service; commands and sagas stay on RabbitMQ/MassTransit.
/// </summary>
public sealed record StreamEventEnvelope<TPayload>(
    Guid EventId,
    string EventType,
    int EventVersion,
    int SchemaVersion,
    DateTimeOffset OccurredAt,
    string SourceService,
    Guid? TenantId,
    string? AggregateId,
    string PartitionKey,
    string? CorrelationId,
    string? CausationId,
    string? TraceId,
    StreamPrivacyClass PrivacyClass,
    TPayload Payload)
{
    public const int CurrentSchemaVersion = 1;

    public static StreamEventEnvelope<TPayload> Create(
        Guid eventId,
        string eventType,
        int eventVersion,
        DateTimeOffset occurredAt,
        string sourceService,
        Guid? tenantId,
        string? aggregateId,
        string partitionKey,
        StreamPrivacyClass privacyClass,
        TPayload payload,
        string? correlationId = null,
        string? causationId = null,
        string? traceId = null) =>
        new(
            eventId,
            GuardRequired(eventType, nameof(eventType)),
            GuardPositive(eventVersion, nameof(eventVersion)),
            CurrentSchemaVersion,
            occurredAt,
            GuardRequired(sourceService, nameof(sourceService)).ToLowerInvariant(),
            tenantId,
            string.IsNullOrWhiteSpace(aggregateId) ? null : aggregateId.Trim(),
            GuardRequired(partitionKey, nameof(partitionKey)),
            string.IsNullOrWhiteSpace(correlationId) ? null : correlationId.Trim(),
            string.IsNullOrWhiteSpace(causationId) ? null : causationId.Trim(),
            string.IsNullOrWhiteSpace(traceId) ? null : traceId.Trim(),
            privacyClass,
            payload ?? throw new ArgumentNullException(nameof(payload)));

    private static string GuardRequired(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value is required.", name);

        return value.Trim();
    }

    private static int GuardPositive(int value, string name)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(name, value, "Value must be positive.");

        return value;
    }
}
