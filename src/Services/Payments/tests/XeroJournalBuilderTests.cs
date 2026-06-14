using ThreeCommerce.Payments.Domain.Ledger;
using ThreeCommerce.Payments.Infrastructure.Xero;

namespace ThreeCommerce.Payments.Tests;

public class XeroJournalBuilderTests
{
    private static JournalLine Debit(string acct, long minor) => new() { Id = Guid.CreateVersion7(), AccountCode = acct, DebitMinor = minor, CreditMinor = 0 };
    private static JournalLine Credit(string acct, long minor) => new() { Id = Guid.CreateVersion7(), AccountCode = acct, DebitMinor = 0, CreditMinor = minor };

    [Fact]
    public void Daily_journal_groups_by_account_and_nets_to_zero()
    {
        // Two sales: cash debited 200.00, revenue credited 168.00, tax credited 32.00.
        var lines = new[]
        {
            Debit(Accounts.CashStripe, 10000), Credit(Accounts.RevenueSales, 8400), Credit(Accounts.LiabilityTaxCollected, 1600),
            Debit(Accounts.CashStripe, 10000), Credit(Accounts.RevenueSales, 8400), Credit(Accounts.LiabilityTaxCollected, 1600),
        };

        var journal = XeroJournalBuilder.BuildDaily(new DateOnly(2026, 6, 13), lines);

        Assert.NotNull(journal);
        Assert.True(journal!.IsBalanced); // nets to zero
        Assert.Equal(200.00m, journal.Lines.Single(l => l.AccountCode == Accounts.CashStripe).LineAmount);
        Assert.Equal(-168.00m, journal.Lines.Single(l => l.AccountCode == Accounts.RevenueSales).LineAmount);
        Assert.Equal(-32.00m, journal.Lines.Single(l => l.AccountCode == Accounts.LiabilityTaxCollected).LineAmount);
    }

    [Fact]
    public void Zero_activity_day_produces_no_journal()
    {
        Assert.Null(XeroJournalBuilder.BuildDaily(new DateOnly(2026, 6, 13), []));
    }

    [Fact]
    public void Refund_journal_is_balanced()
    {
        var lines = new[]
        {
            Debit(Accounts.RevenueRefunds, 8400), Debit(Accounts.LiabilityTaxCollected, 1600), Credit(Accounts.CashStripe, 10000),
        };
        var journal = XeroJournalBuilder.BuildRefund(Guid.CreateVersion7(), new DateOnly(2026, 6, 13), lines);
        Assert.True(journal.IsBalanced);
    }
}
