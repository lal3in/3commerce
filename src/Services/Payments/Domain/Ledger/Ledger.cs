namespace ThreeCommerce.Payments.Domain.Ledger;

/// <summary>
/// Factory for balanced journal entries. Centralizes the accounting so callers
/// cannot construct an unbalanced posting; the DB constraint is the backstop.
/// </summary>
public static class Ledger
{
    /// <summary>
    /// A sale: cash in = net revenue + tax collected. The processing fee (if any) is a separate
    /// expense reducing cash. Cash and fees post to the settling provider's accounts
    /// (<c>cash.{provider}</c> / <c>expense.{provider}_fees</c>), and the description records the
    /// shopper's method as "via {MethodKind}" so the admin ledger shows what was actually used.
    /// </summary>
    public static JournalEntry Sale(
        Guid orderId,
        long grossMinor,
        long taxMinor,
        long feeMinor,
        string currency,
        DateTimeOffset now,
        PaymentMethodKind methodKind = PaymentMethodKind.Card,
        string? provider = null)
    {
        var netMinor = grossMinor - taxMinor;
        var cash = Accounts.CashFor(provider);
        var entry = NewEntry($"Sale for order {orderId} via {methodKind}", orderId.ToString(), currency, now);

        Debit(entry, cash, grossMinor);
        Credit(entry, Accounts.RevenueSales, netMinor);
        if (taxMinor > 0)
        {
            Credit(entry, Accounts.LiabilityTaxCollected, taxMinor);
        }

        if (feeMinor > 0)
        {
            Debit(entry, Accounts.FeesFor(provider), feeMinor);
            Credit(entry, cash, feeMinor);
        }

        return entry;
    }

    /// <summary>A refund reverses the sale: money out of cash, contra-revenue and tax back.</summary>
    public static JournalEntry Refund(
        Guid refundId,
        Guid orderId,
        long grossMinor,
        long taxMinor,
        string currency,
        DateTimeOffset now,
        PaymentMethodKind methodKind = PaymentMethodKind.Card,
        string? provider = null)
    {
        var netMinor = grossMinor - taxMinor;
        var entry = NewEntry($"Refund {refundId} for order {orderId} via {methodKind}", refundId.ToString(), currency, now);

        Debit(entry, Accounts.RevenueRefunds, netMinor);
        if (taxMinor > 0)
        {
            Debit(entry, Accounts.LiabilityTaxCollected, taxMinor);
        }

        Credit(entry, Accounts.CashFor(provider), grossMinor);
        return entry;
    }

    private static JournalEntry NewEntry(string description, string reference, string currency, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Description = description,
            Reference = reference,
            Currency = currency,
            CreatedAt = now,
        };

    private static void Debit(JournalEntry entry, string account, long minor) =>
        entry.Lines.Add(new JournalLine { Id = Guid.CreateVersion7(), EntryId = entry.Id, AccountCode = account, DebitMinor = minor, CreditMinor = 0 });

    private static void Credit(JournalEntry entry, string account, long minor) =>
        entry.Lines.Add(new JournalLine { Id = Guid.CreateVersion7(), EntryId = entry.Id, AccountCode = account, DebitMinor = 0, CreditMinor = minor });
}
