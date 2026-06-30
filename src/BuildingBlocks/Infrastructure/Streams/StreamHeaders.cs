using ThreeCommerce.BuildingBlocks.Contracts.Streams;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

internal static class StreamHeaders
{
    public static IReadOnlyDictionary<string, string> FromEnvelope<TPayload>(StreamEventEnvelope<TPayload> envelope)
    {
        var headers = new Dictionary<string, string>
        {
            ["event-id"] = envelope.EventId.ToString(),
            ["event-type"] = envelope.EventType,
            ["event-version"] = envelope.EventVersion.ToString(),
            ["schema-version"] = envelope.SchemaVersion.ToString(),
            ["source-service"] = envelope.SourceService,
            ["privacy-class"] = envelope.PrivacyClass.ToString(),
        };

        if (envelope.TenantId is { } tenantId)
            headers["tenant-id"] = tenantId.ToString();
        if (!string.IsNullOrWhiteSpace(envelope.CorrelationId))
            headers["correlation-id"] = envelope.CorrelationId;
        if (!string.IsNullOrWhiteSpace(envelope.CausationId))
            headers["causation-id"] = envelope.CausationId;
        if (!string.IsNullOrWhiteSpace(envelope.TraceId))
            headers["trace-id"] = envelope.TraceId;

        return headers;
    }
}
