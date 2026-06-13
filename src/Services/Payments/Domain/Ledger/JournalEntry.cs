namespace ThreeCommerce.Payments.Domain.Ledger;

/// <summary>
/// One balanced double-entry posting (Σ debits = Σ credits, DB-enforced). Append-only:
/// corrections are new reversing entries, never updates (ADR-0014, NFR-1).
/// </summary>
public class JournalEntry
{
    public Guid Id { get; init; }
    public required string Description { get; init; }
    /// <summary>Business reference, e.g. order or refund id, for reconciliation.</summary>
    public required string Reference { get; init; }
    public required string Currency { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public List<JournalLine> Lines { get; init; } = [];
}

public class JournalLine
{
    public Guid Id { get; init; }
    public Guid EntryId { get; init; }
    public required string AccountCode { get; init; }
    /// <summary>Exactly one of Debit/Credit is non-zero; both are non-negative minor units.</summary>
    public long DebitMinor { get; init; }
    public long CreditMinor { get; init; }
}
