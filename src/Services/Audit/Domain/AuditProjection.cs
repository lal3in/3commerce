namespace ThreeCommerce.Audit.Domain;

/// <summary>
/// A projected copy of a service's local audit entry (mt6_1 central projection). The owning service's
/// local hash-chained log is authoritative; this is a searchable cross-service read model. Deduped by
/// (TenantId, Sequence) — the per-tenant chain position the owning service assigned.
/// </summary>
public sealed class AuditProjection
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public long Sequence { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public Guid? ActorId { get; init; }
    public string? ActorRole { get; init; }
    public required string Action { get; init; }
    public required string ResourceType { get; init; }
    public required string ResourceId { get; init; }
    public required string Outcome { get; init; }
    public string? Summary { get; init; }
    public required string Hash { get; init; }
}
