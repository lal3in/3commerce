using Microsoft.Extensions.Logging;
using ThreeCommerce.Payments.Domain.Xero;

namespace ThreeCommerce.Payments.Infrastructure.Xero;

/// <summary>
/// v1 client (no Xero org yet, ADR-0015): logs the journal and returns a synthetic id. The
/// real OAuth2 client swaps in behind IXeroClient without touching the job/consumer.
/// </summary>
public sealed class LoggingXeroClient(ILogger<LoggingXeroClient> logger) : IXeroClient
{
    public Task<string> PostManualJournalAsync(XeroManualJournal journal, CancellationToken ct)
    {
        if (!journal.IsBalanced)
        {
            throw new InvalidOperationException("Refusing to post an unbalanced journal to Xero.");
        }

        var id = $"xero_{Guid.CreateVersion7():N}";
        logger.LogInformation("XERO journal {Id}: {Narration} ({LineCount} lines, nets to zero)",
            id, journal.Narration, journal.Lines.Count);
        return Task.FromResult(id);
    }
}
