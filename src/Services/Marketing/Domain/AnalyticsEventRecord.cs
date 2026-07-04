namespace ThreeCommerce.Marketing.Domain;

/// <summary>
/// A persisted analytics event (def_4 / mt5_5 — the storage half of the mt5_4 collector).
/// Append-only; deduped per tenant by the client-generated <see cref="EventId"/>, so the
/// storefront batcher can retry a batch without double-counting. Never stores a raw IP
/// (see <see cref="IpAnonymizer"/>) and payloads arrive pre-sanitized by
/// <see cref="AnalyticsPayload.Sanitize"/>.
/// </summary>
public class AnalyticsEventRecord
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public int SchemaVersion { get; init; }
    public required string EventType { get; init; }
    public string? VisitorId { get; init; }
    public string? SessionId { get; init; }
    public Guid? CustomerId { get; init; }
    public bool AnalyticsConsent { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public required string EventId { get; init; }
    public required string PayloadJson { get; init; }
    public required string ClientIpCoarse { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }
}
