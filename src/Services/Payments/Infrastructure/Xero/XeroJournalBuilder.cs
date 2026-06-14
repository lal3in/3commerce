using ThreeCommerce.Payments.Domain.Ledger;
using ThreeCommerce.Payments.Domain.Xero;

namespace ThreeCommerce.Payments.Infrastructure.Xero;

/// <summary>
/// Pure logic (ADR-0017): summarize a day's ledger into one balanced Xero manual journal —
/// group lines by account, net debit−credit, convert minor units → decimal at the boundary.
/// Because the ledger is balanced, the journal nets to zero.
/// </summary>
public static class XeroJournalBuilder
{
    public static XeroManualJournal? BuildDaily(DateOnly date, IEnumerable<JournalLine> lines)
    {
        var byAccount = lines
            .GroupBy(l => l.AccountCode)
            .Select(g => new XeroJournalLine(
                g.Key,
                ToDecimal(g.Sum(l => l.DebitMinor) - g.Sum(l => l.CreditMinor)),
                $"Daily summary {date:yyyy-MM-dd}"))
            .Where(l => l.LineAmount != 0m)
            .ToList();

        // Nothing happened that day → no journal (don't post an empty one).
        return byAccount.Count == 0 ? null : new XeroManualJournal($"3commerce daily summary {date:yyyy-MM-dd}", date, byAccount);
    }

    public static XeroManualJournal BuildRefund(Guid refundId, DateOnly date, IEnumerable<JournalLine> refundLines)
    {
        var lines = refundLines
            .Select(l => new XeroJournalLine(l.AccountCode, ToDecimal(l.DebitMinor - l.CreditMinor), $"Refund {refundId}"))
            .Where(l => l.LineAmount != 0m)
            .ToList();
        return new XeroManualJournal($"3commerce refund {refundId}", date, lines);
    }

    private static decimal ToDecimal(long minor) => minor / 100m;
}
