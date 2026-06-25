namespace ThreeCommerce.BuildingBlocks.Infrastructure.Audit;

/// <summary>
/// A local audit entry was recorded (mt6_1). Published by AuditRecorder (when a publisher is wired) so
/// the central Audit service can project it for cross-service search. The local log stays authoritative.
/// Co-located with the audit framework rather than Contracts to avoid an Infrastructure→Contracts edge.
/// </summary>
public record AuditEntryRecorded(
    Guid TenantId, long Sequence, DateTimeOffset OccurredAt, Guid? ActorId, string? ActorRole,
    string Action, string ResourceType, string ResourceId, string Outcome, string? Summary, string Hash);
