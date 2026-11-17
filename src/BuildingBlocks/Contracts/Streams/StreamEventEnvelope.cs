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
    TPayload Payload,
    // W3C trace context of the operation that committed the fact (ADR-0034 amendment, 2026-11-15, Q6): additive
    // and optional, so envelopes written before it still parse (null). Stamped by the stream outbox stager /
    // producer from the current activity; a stream consumer starts its span as a child of it.
    string? TraceParent = null,
    string? TraceState = null)
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
        string? traceId = null,
        string? traceParent = null,
        string? traceState = null) =>
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
            payload ?? throw new ArgumentNullException(nameof(payload)),
            string.IsNullOrWhiteSpace(traceParent) ? null : traceParent.Trim(),
            string.IsNullOrWhiteSpace(traceState) ? null : traceState.Trim());

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
