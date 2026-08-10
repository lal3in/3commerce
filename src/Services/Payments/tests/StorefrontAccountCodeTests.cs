using ThreeCommerce.Payments.Domain.Ledger;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// ledger_sf foundation: the per-storefront account-code scheme covering every role, so a movement can be
/// attributed to a storefront with no shared/default fallback. Codes are deterministic from the storefront
/// id (+ settling provider for cash/fees).
/// </summary>
public class StorefrontAccountCodeTests
{
    private static readonly Guid Sid = Guid.Parse("3f2a0000-0000-0000-0000-0000000000aa");
    private static string N => Sid.ToString("N");

    [Fact]
    public void Cash_and_fees_are_scoped_to_store_and_provider()
    {
        Assert.Equal($"cash.store-{N}.stripe", Accounts.CashStoreFor(Sid, "stripe"));
        Assert.Equal($"expense.store-{N}.stripe_fees", Accounts.FeesStoreFor(Sid, "stripe"));
        Assert.Equal($"expense.store-{N}.stripe_chargeback_fees", Accounts.ChargebackFeesStoreFor(Sid, "stripe"));
        // Polar settles to its own per-store cash account, distinct from Stripe's.
        Assert.Equal($"cash.store-{N}.polar", Accounts.CashStoreFor(Sid, "polar"));
    }

    [Fact]
    public void Blank_or_mock_provider_collapses_to_the_default_but_stays_store_scoped()
    {
        Assert.Equal($"cash.store-{N}.stripe", Accounts.CashStoreFor(Sid, null));
        Assert.Equal($"cash.store-{N}.stripe", Accounts.CashStoreFor(Sid, "mock"));
    }

    [Fact]
    public void Store_scoped_liability_and_contra_accounts()
    {
        Assert.Equal($"revenue.refunds.store-{N}", Accounts.RefundsStoreFor(Sid));
        Assert.Equal($"liability.supplier_payable.store-{N}", Accounts.SupplierPayableStoreFor(Sid));
        Assert.Equal($"liability.carrier_payable.store-{N}", Accounts.CarrierPayableStoreFor(Sid));
    }

    [Fact]
    public void Every_role_resolves_to_a_distinct_store_scoped_code()
    {
        var codes = new[]
        {
            Accounts.CashStoreFor(Sid, "stripe"),
            Accounts.FeesStoreFor(Sid, "stripe"),
            Accounts.ChargebackFeesStoreFor(Sid, "stripe"),
            Accounts.RefundsStoreFor(Sid),
            Accounts.SupplierPayableStoreFor(Sid),
            Accounts.CarrierPayableStoreFor(Sid),
            Accounts.CogsStoreFor(Sid),
            Accounts.ShippingCostStoreFor(Sid),
            Accounts.WriteoffsStoreFor(Sid),
        };

        Assert.Equal(codes.Length, codes.Distinct().Count());       // all distinct
        Assert.All(codes, c => Assert.Contains($"store-{N}", c));   // all store-attributed
    }
}
