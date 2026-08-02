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
    // Shared shipping-income account (fallback when a sale isn't storefront-attributed). Per-storefront
    // sales post to the store's own shipping.store-{id}; both live under the shipping.* prefix so the P&L
    // reports shipping separately from product revenue. Credit-normal, reversed proportionally on refund.
    public const string ShippingIncome = "shipping.income";
    public const string ExpenseStripeFees = "expense.stripe_fees";
    public const string ExpenseCostOfGoodsSold = "expense.cogs";
    public const string CostOfGoodsSold = ExpenseCostOfGoodsSold;
    public const string LiabilityTaxCollected = "liability.tax_collected";
    public const string LiabilitySupplierPayable = "liability.supplier_payable";
    // Shared fallback for carrier label cost when a package isn't storefront-attributed. Per-storefront
    // labels post to the store's own expense.shipping.store-{id} (see ShippingCostStoreFor); both live
    // under the expense.shipping* prefix so Financials can sum carrier cost as one P&L line.
    public const string ExpenseShippingCarrier = "expense.shipping_carrier";
    // Shared fallback for RMA Storage dispositions (Damage/Incomplete/UnfitForSale) when the order
    // isn't storefront-attributed. Per-storefront write-offs post to expense.writeoffs.store-{id}
    // (see WriteoffsStoreFor); both live under the expense.writeoffs prefix.
    public const string ExpenseWriteoffs = "expense.writeoffs";
    // Mirrors LiabilitySupplierPayable's shape for carrier costs: no real money moves in dev, so the
    // carrier-cost accrual credits this liability instead of cash, keeping cash.* truthful to PSP
    // settlements only.
    public const string LiabilityCarrierPayable = "liability.carrier_payable";

    /// <summary>
    /// The cash account settling through <paramref name="provider"/>: <c>cash.{provider}</c>.
    /// "stripe" (and any blank/unknown provider) keeps <see cref="CashStripe"/>, so existing data,
    /// the seeded chart of accounts and every pre-pay_4 posting stay coherent.
    /// </summary>
    public static string CashFor(string? provider) => $"cash.{LedgerProviders.Normalize(provider)}";

    /// <summary>The processing-fee expense account for <paramref name="provider"/>: <c>expense.{provider}_fees</c>.</summary>
    public static string FeesFor(string? provider) => $"expense.{LedgerProviders.Normalize(provider)}_fees";

    /// <summary>
    /// The per-storefront COGS account for store <paramref name="sid"/>: <c>expense.cogs.store-{id:N}</c>.
    /// Derived in Payments (not operator-configurable, unlike income-side codes) from the order's
    /// StorefrontId; orders with no storefront attribution fall back to the shared <see cref="ExpenseCostOfGoodsSold"/>.
    /// </summary>
    public static string CogsStoreFor(Guid sid) => $"expense.cogs.store-{sid:N}";

    /// <summary>
    /// The per-storefront carrier-cost account for store <paramref name="sid"/>: <c>expense.shipping.store-{id:N}</c>.
    /// Derived in Payments from the package's order StorefrontId; unattributed packages fall back to
    /// the shared <see cref="ExpenseShippingCarrier"/>.
    /// </summary>
    public static string ShippingCostStoreFor(Guid sid) => $"expense.shipping.store-{sid:N}";

    /// <summary>
    /// The per-storefront write-off account for store <paramref name="sid"/>: <c>expense.writeoffs.store-{id:N}</c>.
    /// Derived in Payments from the RMA's order StorefrontId; unattributed orders fall back to the
    /// shared <see cref="ExpenseWriteoffs"/>.
    /// </summary>
    public static string WriteoffsStoreFor(Guid sid) => $"expense.writeoffs.store-{sid:N}";
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
