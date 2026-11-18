using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;

namespace ThreeCommerce.Payments.Infrastructure;

/// <summary>
/// The OSS part of a refund or chargeback of a payment (ADR-0063 § 16, per-destination OSS tax accounts): the reversal's
/// tax comes back from the accounts the sale credited — each destination's OSS account in proportion to what it was
/// booked, never past what is still standing on it, and the final reversal returns each destination's exact remainder
/// (<see cref="OssTaxSplit.Reversal"/>). A payment without an OSS part reverses exactly as before.
/// </summary>
public static class OssTaxReversals
{
    public static async Task<OssTaxSplit> ForAsync(
        PaymentsDbContext db, Payment payment, string? taxAccount, long reversedTaxMinor, bool final, CancellationToken ct)
    {
        var booked = payment.OssTaxShares;
        if (booked.IsEmpty || reversedTaxMinor <= 0)
        {
            return OssTaxSplit.None;
        }

        // What the payment's earlier refunds already took back from its OSS accounts (each refund's entry is referenced by
        // the refund id). Read from the ledger itself, so the remainder is exact whatever each refund rounded to.
        var ossPrefix = (string.IsNullOrWhiteSpace(taxAccount) ? Accounts.LiabilityTaxCollected : taxAccount) + Accounts.OssTaxSuffix;
        var refundReferences = await db.Refunds.AsNoTracking()
            .Where(r => r.PaymentIntentId == payment.PaymentIntentId)
            .Select(r => r.Id.ToString())
            .ToListAsync(ct);
        var reversedLines = refundReferences.Count == 0
            ? []
            : await db.JournalEntries.AsNoTracking()
                .Where(e => refundReferences.Contains(e.Reference))
                .SelectMany(e => e.Lines)
                .Where(l => l.AccountCode.StartsWith(ossPrefix))
                .Select(l => new { l.AccountCode, Net = l.DebitMinor - l.CreditMinor })
                .ToListAsync(ct);
        var already = OssTaxSplit.Of(reversedLines.Select(l => ((string?)l.AccountCode[ossPrefix.Length..], l.Net)));
        return booked.Reversal(reversedTaxMinor, payment.TaxMinor, already, final);
    }
}
