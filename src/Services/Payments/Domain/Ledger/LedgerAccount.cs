namespace ThreeCommerce.Payments.Domain.Ledger;

/// <summary>Chart-of-accounts entry. Seeded once; the ledger references accounts by Code.</summary>
public class LedgerAccount
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public AccountType Type { get; init; }
}

public enum AccountType
{
    Asset = 1,
    Liability = 2,
    Revenue = 3,
    Expense = 4,
}

public static class Accounts
{
    public const string CashStripe = "cash.stripe";
    public const string RevenueSales = "revenue.sales";
    public const string RevenueRefunds = "revenue.refunds";
    public const string ExpenseStripeFees = "expense.stripe_fees";
    public const string ExpenseCostOfGoodsSold = "expense.cogs";
    public const string CostOfGoodsSold = ExpenseCostOfGoodsSold;
    public const string LiabilityTaxCollected = "liability.tax_collected";
    public const string LiabilitySupplierPayable = "liability.supplier_payable";

    /// <summary>
    /// The cash account settling through <paramref name="provider"/>: <c>cash.{provider}</c>.
    /// "stripe" (and any blank/unknown provider) keeps <see cref="CashStripe"/>, so existing data,
    /// the seeded chart of accounts and every pre-pay_4 posting stay coherent.
    /// </summary>
    public static string CashFor(string? provider) => $"cash.{LedgerProviders.Normalize(provider)}";

    /// <summary>The processing-fee expense account for <paramref name="provider"/>: <c>expense.{provider}_fees</c>.</summary>
    public static string FeesFor(string? provider) => $"expense.{LedgerProviders.Normalize(provider)}_fees";
}

/// <summary>
/// The provider keys the ledger knows how to attribute cash/fees to — the pay_4 adapter set
/// registered in Payments' Program.cs (stripe, polar, paypal, afterpay) plus the offline "mock"
/// adapter, which settles into the stripe accounts because LocalMock moves no real money and dev
/// data must stay on the seeded cash.stripe.
/// </summary>
public static class LedgerProviders
{
    public const string Default = "stripe";

    /// <summary>Every provider that gets its own cash/fee account pair in the chart of accounts.</summary>
    public static readonly IReadOnlyList<string> Known = ["stripe", "polar", "paypal", "afterpay"];

    /// <summary>
    /// Lowercases and validates a provider key, collapsing null/blank/unknown/"mock" to
    /// <see cref="Default"/> so a posting can never land on an unseeded account code.
    /// </summary>
    public static string Normalize(string? provider)
    {
        var key = (provider ?? string.Empty).Trim().ToLowerInvariant();
        return Known.Contains(key) ? key : Default;
    }
}
