using System.Security.Cryptography;
using System.Text;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Audit;

public enum AuditOutcome { Success = 1, Denied = 2, Failure = 3 }

/// <summary>
/// What happened, captured for an audit entry (mt6_1). Keep <see cref="Summary"/> short and free of
/// PII/secrets — it is a human-readable label, not a payload dump.
/// </summary>
public sealed record AuditDraft(
    Guid TenantId,
    string Action,
    string ResourceType,
    string ResourceId,
    AuditOutcome Outcome,
    Guid? ActorId = null,
    string? ActorRole = null,
    string? Summary = null);

/// <summary>
/// One append-only, hash-chained audit record (mt6_1). The local per-service log is authoritative;
/// a central Audit service projects it later. <see cref="Hash"/> = SHA-256 over the entry's fields
/// plus the previous entry's hash, so any edit/insert/delete breaks the chain (tamper-evident).
/// </summary>
public sealed record AuditEntry
{
    public const string Genesis = "GENESIS";

    // ASCII unit separator (U+001F) — an unambiguous field delimiter for the canonical content.
    private static readonly char Sep = (char)0x1f;

    public Guid Id { get; init; }
    public Guid TenantId { get; init; }

    /// <summary>Per-tenant monotonic sequence (1-based). The chain is per tenant.</summary>
    public long Sequence { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public Guid? ActorId { get; init; }
    public string? ActorRole { get; init; }
    public required string Action { get; init; }
    public required string ResourceType { get; init; }
    public required string ResourceId { get; init; }
    public AuditOutcome Outcome { get; init; }
    public string? Summary { get; init; }
    public required string PrevHash { get; init; }
    public required string Hash { get; init; }

    /// <summary>The canonical content that is hashed (everything except Id and Hash itself).</summary>
    public string CanonicalContent() => string.Join(
        Sep,
        TenantId,
        Sequence,
        OccurredAt.ToUniversalTime().ToString("O"),
        ActorId?.ToString() ?? string.Empty,
        ActorRole ?? string.Empty,
        Action,
        ResourceType,
        ResourceId,
        (int)Outcome,
        Summary ?? string.Empty,
        PrevHash);

    public string ComputeHash() =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalContent())));
}
