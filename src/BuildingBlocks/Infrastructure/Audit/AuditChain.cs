namespace ThreeCommerce.BuildingBlocks.Infrastructure.Audit;

/// <summary>The result of verifying a tenant's audit chain (mt6_1).</summary>
public readonly record struct AuditVerification(bool Intact, long? FirstBrokenSequence)
{
    public static readonly AuditVerification Ok = new(true, null);

    public static AuditVerification BrokenAt(long sequence) => new(false, sequence);
}

/// <summary>
/// Pure hash-chain logic for the local audit log (mt6_1): append the next entry, and verify an
/// existing chain is intact. No I/O — the store supplies the previous entry / the full chain.
/// </summary>
public static class AuditChain
{
    /// <summary>Build the next entry, chaining off <paramref name="previous"/> (null = genesis).</summary>
    public static AuditEntry Append(AuditEntry? previous, AuditDraft draft, DateTimeOffset now)
    {
        var entry = new AuditEntry
        {
            Id = Guid.CreateVersion7(),
            TenantId = draft.TenantId,
            Sequence = (previous?.Sequence ?? 0) + 1,
            OccurredAt = now,
            ActorId = draft.ActorId,
            ActorRole = draft.ActorRole,
            Action = draft.Action,
            ResourceType = draft.ResourceType,
            ResourceId = draft.ResourceId,
            Outcome = draft.Outcome,
            Summary = draft.Summary,
            PrevHash = previous?.Hash ?? AuditEntry.Genesis,
            Hash = string.Empty,
        };

        return entry with { Hash = entry.ComputeHash() };
    }

    /// <summary>
    /// Recompute the chain and report the first entry whose stored hash, link, or sequence does not
    /// reconcile — i.e. an edit, insertion, or deletion. <paramref name="ordered"/> must be ascending
    /// by sequence.
    /// </summary>
    public static AuditVerification Verify(IReadOnlyList<AuditEntry> ordered)
    {
        var expectedPrevHash = AuditEntry.Genesis;
        long expectedSequence = 1;

        foreach (var entry in ordered)
        {
            if (entry.Sequence != expectedSequence
                || entry.PrevHash != expectedPrevHash
                || entry.Hash != entry.ComputeHash())
            {
                return AuditVerification.BrokenAt(entry.Sequence);
            }

            expectedPrevHash = entry.Hash;
            expectedSequence++;
        }

        return AuditVerification.Ok;
    }
}
