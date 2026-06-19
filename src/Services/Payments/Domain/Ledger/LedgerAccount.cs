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
}
