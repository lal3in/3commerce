namespace ThreeCommerce.Payments.Domain.Xero;

/// <summary>A Xero manual journal — provider-agnostic; the client maps it to the Xero API.</summary>
public record XeroManualJournal(string Narration, DateOnly Date, IReadOnlyList<XeroJournalLine> Lines)
{
    /// <summary>Xero rejects journals that don't net to zero.</summary>
    public bool IsBalanced => Lines.Sum(l => l.LineAmount) == 0m;
}

/// <summary>Positive LineAmount = debit, negative = credit (Xero convention for manual journals).</summary>
public record XeroJournalLine(string AccountCode, decimal LineAmount, string Description);

public enum SyncRunStatus { Pending = 1, Posted = 2, Failed = 3, Skipped = 4 }

/// <summary>One Xero posting attempt (nightly summary or per-refund), for monitoring + idempotency.</summary>
public class SyncRun
{
    public Guid Id { get; init; }
    /// <summary>Idempotency key: "daily:yyyy-MM-dd" or "refund:{refundId}".</summary>
    public required string Reference { get; init; }
    public SyncRunStatus Status { get; set; }
    public string? XeroJournalId { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>The Xero seam (ADR-0017). v1 logs; a real OAuth2 client swaps in once a Xero org exists.</summary>
public interface IXeroClient
{
    public Task<string> PostManualJournalAsync(XeroManualJournal journal, CancellationToken ct);
}
