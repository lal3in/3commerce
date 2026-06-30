namespace ThreeCommerce.BuildingBlocks.Infrastructure.Audit;

/// <summary>
/// Kafka audit stream payload. Keep this hash/reference oriented: summaries are labels/reasons only,
/// never sensitive field values, card data, bank details, tokens, or raw request bodies.
/// </summary>
public sealed record AuditEntryStreamPayload(
    long Sequence,
    DateTimeOffset OccurredAt,
    Guid? ActorId,
    string? ActorRole,
    string Action,
    string ResourceType,
    string ResourceId,
    string Outcome,
    string? Summary,
    string Hash);
