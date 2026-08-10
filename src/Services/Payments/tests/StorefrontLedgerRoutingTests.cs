using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// ledger_sf_2: when a posting is storefront-attributed, cash, processing/chargeback fees and the
/// refund/chargeback contra route to the STORE's own accounts (not the shared cash.{provider} /
/// expense.{provider}_fees / revenue.refunds). Provided a storefront id, no line touches a shared account.
/// </summary>
public class StorefrontLedgerRoutingTests
{
    private static readonly Guid Sid = Guid.Parse("3f2a0000-0000-0000-0000-0000000000bb");
    private const string Prov = "stripe";

    private static long Debits(JournalEntry e, string a) => e.Lines.Where(l => l.AccountCode == a).Sum(l => l.DebitMinor);
    private static long Credits(JournalEntry e, string a) => e.Lines.Where(l => l.AccountCode == a).Sum(l => l.CreditMinor);
    private static void AssertBalanced(JournalEntry e) => Assert.Equal(e.Lines.Sum(l => l.DebitMinor), e.Lines.Sum(l => l.CreditMinor));

    private static string Rev => $"revenue.store-{Sid:N}";
    private static string Tax => $"tax.store-{Sid:N}";
    private static string Recv => $"receivable.store-{Sid:N}";
    private static string Ship => $"shipping.store-{Sid:N}";

    [Fact]
    public void A_storefront_sale_routes_cash_and_fees_to_the_stores_own_accounts()
    {
        var entry = Ledger.Sale(Guid.CreateVersion7(), 10000, 900, 300, "AUD", DateTimeOffset.UtcNow,
            PaymentMethodKind.Card, Prov, Rev, Tax, Recv, shippingMinor: 500, shippingAccount: Ship, storefrontId: Sid);

        Assert.Equal(10000, Debits(entry, Accounts.CashStoreFor(Sid, Prov)));   // cash settled per store
        Assert.Equal(300, Credits(entry, Accounts.CashStoreFor(Sid, Prov)));    // fee out of the same store cash
        Assert.Equal(300, Debits(entry, Accounts.FeesStoreFor(Sid, Prov)));     // fee expense per store
        Assert.Equal(8600, Credits(entry, Rev));                                // net revenue to the store
        Assert.Equal(0, Debits(entry, Accounts.CashStripe));                    // shared cash untouched
        Assert.Equal(0, Debits(entry, Accounts.FeesFor(Prov)));                 // shared fees untouched
        AssertBalanced(entry);
    }

    [Fact]
    public void A_storefront_chargeback_routes_cash_and_dispute_fee_to_the_store()
    {
        var entry = Ledger.Chargeback(Guid.CreateVersion7(), 10000, 900, 1500, "AUD", DateTimeOffset.UtcNow,
            PaymentMethodKind.Card, Prov, Rev, Tax, Recv, shippingMinor: 500, shippingAccount: Ship, storefrontId: Sid);

        Assert.Equal(11500, Credits(entry, Accounts.CashStoreFor(Sid, Prov)));  // disputed gross + fee out of store cash
        Assert.Equal(1500, Debits(entry, Accounts.ChargebackFeesStoreFor(Sid, Prov)));
        Assert.Equal(0, Credits(entry, Accounts.CashStripe));                   // shared cash untouched
        Assert.Equal(0, Debits(entry, Accounts.ChargebackFeesFor(Prov)));       // shared chargeback fee untouched
        AssertBalanced(entry);
    }

    [Fact]
    public void A_storefront_refund_without_a_receivable_uses_the_store_contra()
    {
        // No receivable account provided → the legacy branch, but still store-attributed.
        var entry = Ledger.Refund(Guid.CreateVersion7(), Guid.CreateVersion7(), 5000, 0, "USD", DateTimeOffset.UtcNow,
            PaymentMethodKind.Card, Prov, storefrontId: Sid);

        Assert.Equal(5000, Debits(entry, Accounts.RefundsStoreFor(Sid)));       // store contra, not shared revenue.refunds
        Assert.Equal(5000, Credits(entry, Accounts.CashStoreFor(Sid, Prov)));   // store cash
        Assert.Equal(0, Debits(entry, Accounts.RevenueRefunds));               // shared contra untouched
        AssertBalanced(entry);
    }

    [Fact]
    public void A_sale_honours_a_custom_reference_and_description_for_non_order_revenue()
    {
        // pay_*_posts_revenue: a subscription renewal / usage charge posts a Sale under its OWN reference
        // + description, so it never collides with the order's sale entry (whose reference is the order id).
        var orderId = Guid.CreateVersion7();
        var entry = Ledger.Sale(orderId, 1500, 0, 0, "EUR", DateTimeOffset.UtcNow,
            PaymentMethodKind.Card, Prov, Rev, Tax, Recv, storefrontId: Sid,
            reference: "renew-abc-2", description: "Subscription renewal abc period 2");

        Assert.Equal("renew-abc-2", entry.Reference);
        Assert.NotEqual(orderId.ToString(), entry.Reference);
        Assert.Equal("Subscription renewal abc period 2", entry.Description);
        Assert.Equal(1500, Credits(entry, Rev)); // still routes to the store's own revenue
        AssertBalanced(entry);
    }

    [Fact]
    public void A_storefront_sale_posts_no_line_to_a_shared_account()
    {
        var entry = Ledger.Sale(Guid.CreateVersion7(), 10000, 900, 300, "AUD", DateTimeOffset.UtcNow,
            PaymentMethodKind.Card, Prov, Rev, Tax, Recv, shippingMinor: 500, shippingAccount: Ship, storefrontId: Sid);

        var shared = new[]
        {
            Accounts.RevenueSales, Accounts.RevenueRefunds, Accounts.ShippingIncome, Accounts.LiabilityTaxCollected,
            Accounts.CashStripe, Accounts.CashFor(Prov), Accounts.FeesFor(Prov),
        };
        Assert.DoesNotContain(entry.Lines, l => shared.Contains(l.AccountCode));
    }
}
