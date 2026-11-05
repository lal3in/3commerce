namespace ThreeCommerce.BuildingBlocks.Infrastructure.Audit;

/// <summary>
/// The result of verifying a tenant's audit chain (mt6_1). <see cref="LegacyPrecisionEntries"/> counts entries
/// written before the stored-precision fix (ADR-0029 amendment 2026-11-04) whose hash was taken over a 100 ns
/// <c>OccurredAt</c> that Postgres truncated: each was still fully re-verified, from the one sub-microsecond
/// original it can have come from — reported so a verifier can see how much of the chain predates the fix.
/// </summary>
public readonly record struct AuditVerification(bool Intact, long? FirstBrokenSequence, int LegacyPrecisionEntries = 0)
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
    /// <summary>
    /// Build the next entry, chaining off <paramref name="previous"/> (null = genesis). <paramref name="now"/> is
    /// truncated to whole microseconds (<see cref="AuditEntry.StoredPrecision"/>) BEFORE hashing, so the entry in
    /// memory, the published <see cref="AuditEntryRecorded"/> and the row reloaded from Postgres all carry — and hash —
    /// the same time.
    /// </summary>
    public static AuditEntry Append(AuditEntry? previous, AuditDraft draft, DateTimeOffset now)
    {
        var entry = new AuditEntry
        {
            Id = Guid.CreateVersion7(),
            TenantId = draft.TenantId,
            Sequence = (previous?.Sequence ?? 0) + 1,
            OccurredAt = AuditEntry.StoredPrecision(now),
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
    /// <para>
    /// Every entry's content is re-hashed — including entries written before the stored-precision fix, whose
    /// hash covers a 100 ns <c>OccurredAt</c> that Postgres truncated to microseconds (ADR-0029 amendment
    /// 2026-11-04). Npgsql truncates (never rounds), so such an entry's original time is the stored time plus
    /// 1–9 ticks: when the stored time is whole microseconds and the exact hash does not match, those nine
    /// originals are tried (<see cref="MatchesLegacyPrecision"/>). That window accepts no stored value the
    /// exact check would not: it never reaches another microsecond, so moving a stored time by any amount,
    /// or changing any other field, still breaks the chain.
    /// </para>
    /// </summary>
    public static AuditVerification Verify(IReadOnlyList<AuditEntry> ordered)
    {
        var expectedPrevHash = AuditEntry.Genesis;
        long expectedSequence = 1;
        var legacyPrecision = 0;

        foreach (var entry in ordered)
        {
            if (entry.Sequence != expectedSequence || entry.PrevHash != expectedPrevHash)
            {
                return AuditVerification.BrokenAt(entry.Sequence);
            }

            if (entry.Hash != entry.ComputeHash())
            {
                if (!MatchesLegacyPrecision(entry))
                {
                    return AuditVerification.BrokenAt(entry.Sequence);
                }

                legacyPrecision++;
            }

            expectedPrevHash = entry.Hash;
            expectedSequence++;
        }

        return new AuditVerification(true, null, legacyPrecision);
    }

    /// <summary>
    /// True when <paramref name="entry"/> was hashed, before the stored-precision fix, over a 100 ns time that
    /// Postgres then truncated to its stored microsecond: one of stored + 1..9 ticks reproduces its hash. Only a
    /// whole-microsecond time (what Postgres returns) is searched; a sub-microsecond time in memory is never legacy.
    /// </summary>
    private static bool MatchesLegacyPrecision(AuditEntry entry)
    {
        if (entry.OccurredAt.Ticks % TimeSpan.TicksPerMicrosecond != 0)
        {
            return false;
        }

        for (var ticks = 1; ticks < TimeSpan.TicksPerMicrosecond; ticks++)
        {
            if (entry.Hash == (entry with { OccurredAt = entry.OccurredAt.AddTicks(ticks) }).ComputeHash())
            {
                return true;
            }
        }

        return false;
    }
}
