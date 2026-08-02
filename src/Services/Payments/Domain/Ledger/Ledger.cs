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
        string? provider = null,
        string? revenueAccount = null,
        string? taxAccount = null,
        string? receivableAccount = null,
        long shippingMinor = 0,
        string? shippingAccount = null)
    {
        // Product revenue = gross − tax − shipping; shipping is booked to its own income account (the
        // storefront's shipping.store-… when known, else the shared shipping.income) so the P&L reports
        // it as a separate line rather than lumping it into product revenue.
        var shipping = Math.Clamp(shippingMinor, 0, grossMinor - taxMinor);
        var shippingIncome = string.IsNullOrWhiteSpace(shippingAccount) ? Accounts.ShippingIncome : shippingAccount;
        var netMinor = grossMinor - taxMinor - shipping;
        var cash = Accounts.CashFor(provider);
        // Per-storefront books (phase 2): revenue and tax post to the storefront's own accounts when
        // known; otherwise the shared revenue.sales / liability.tax_collected. Cash stays on the
        // settling provider's account (cash.{provider}) — money settles per PSP, not per storefront.
        var revenue = string.IsNullOrWhiteSpace(revenueAccount) ? Accounts.RevenueSales : revenueAccount;
        var taxLiability = string.IsNullOrWhiteSpace(taxAccount) ? Accounts.LiabilityTaxCollected : taxAccount;
        var entry = NewEntry($"Sale for order {orderId} via {methodKind}", orderId.ToString(), currency, now);

        if (string.IsNullOrWhiteSpace(receivableAccount))
        {
            // Legacy / no storefront: cash-basis directly against revenue.
            Debit(entry, cash, grossMinor);
            Credit(entry, revenue, netMinor);
            if (shipping > 0)
            {
                Credit(entry, shippingIncome, shipping);
            }

            if (taxMinor > 0)
            {
                Credit(entry, taxLiability, taxMinor);
            }
        }
        else
        {
            // Per-storefront settlement bridge: recognize the store's revenue + tax against its
            // receivable (the store is owed the gross), then clear the receivable with the cash the
            // platform collected on its behalf into the PSP account. Receivable nets to zero when cash
            // settles with the sale, and carries a balance only while a settlement is outstanding.
            Debit(entry, receivableAccount, grossMinor);
            Credit(entry, revenue, netMinor);
            if (shipping > 0)
            {
                Credit(entry, shippingIncome, shipping);
            }

            if (taxMinor > 0)
            {
                Credit(entry, taxLiability, taxMinor);
            }

            Debit(entry, cash, grossMinor);
            Credit(entry, receivableAccount, grossMinor);
        }

        if (feeMinor > 0)
        {
            Debit(entry, Accounts.FeesFor(provider), feeMinor);
            Credit(entry, cash, feeMinor);
        }

        return entry;
    }

    /// <summary>
    /// A refund reverses the sale: money out of cash, revenue + tax back. Legacy/no-storefront refunds
    /// post the reversal to the shared contra-revenue (revenue.refunds); a storefront refund reverses
    /// the store's OWN revenue/tax through its receivable bridge (mirroring <see cref="Sale"/>), so a
    /// store's books net sales against refunds in the same accounts.
    /// </summary>
    public static JournalEntry Refund(
        Guid refundId,
        Guid orderId,
        long grossMinor,
        long taxMinor,
        string currency,
        DateTimeOffset now,
        PaymentMethodKind methodKind = PaymentMethodKind.Card,
        string? provider = null,
        string? revenueAccount = null,
        string? taxAccount = null,
        string? receivableAccount = null,
        long shippingMinor = 0,
        string? shippingAccount = null)
    {
        // Mirror the sale: reverse product revenue, shipping income and tax separately so each P&L line
        // nets its refunds. shippingMinor is the (proportional) shipping slice of this refund.
        var shipping = Math.Clamp(shippingMinor, 0, grossMinor - taxMinor);
        var shippingIncome = string.IsNullOrWhiteSpace(shippingAccount) ? Accounts.ShippingIncome : shippingAccount;
        var netMinor = grossMinor - taxMinor - shipping;
        var cash = Accounts.CashFor(provider);
        var entry = NewEntry($"Refund {refundId} for order {orderId} via {methodKind}", refundId.ToString(), currency, now);

        if (string.IsNullOrWhiteSpace(receivableAccount))
        {
            Debit(entry, Accounts.RevenueRefunds, netMinor);
            if (shipping > 0)
            {
                Debit(entry, shippingIncome, shipping);
            }

            if (taxMinor > 0)
            {
                Debit(entry, Accounts.LiabilityTaxCollected, taxMinor);
            }

            Credit(entry, cash, grossMinor);
        }
        else
        {
            var revenue = string.IsNullOrWhiteSpace(revenueAccount) ? Accounts.RevenueRefunds : revenueAccount;
            var taxLiability = string.IsNullOrWhiteSpace(taxAccount) ? Accounts.LiabilityTaxCollected : taxAccount;
            Debit(entry, revenue, netMinor);
            if (shipping > 0)
            {
                Debit(entry, shippingIncome, shipping);
            }

            if (taxMinor > 0)
            {
                Debit(entry, taxLiability, taxMinor);
            }

            Credit(entry, receivableAccount, grossMinor);
            Debit(entry, receivableAccount, grossMinor);
            Credit(entry, cash, grossMinor);
        }

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
        entry.Lines.Add(new JournalLine { Id = Guid.CreateVersion7(), EntryId = entry.Id, AccountCode = account, Currency = entry.Currency, DebitMinor = minor, CreditMinor = 0 });

    private static void Credit(JournalEntry entry, string account, long minor) =>
        entry.Lines.Add(new JournalLine { Id = Guid.CreateVersion7(), EntryId = entry.Id, AccountCode = account, Currency = entry.Currency, DebitMinor = 0, CreditMinor = minor });
}
