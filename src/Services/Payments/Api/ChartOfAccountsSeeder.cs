using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Payments.Domain.Ledger;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.Payments.Api;

/// <summary>Seeds the fixed chart of accounts (ADR-0014) once, idempotently.</summary>
public static class ChartOfAccountsSeeder
{
    public static async Task SeedAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        try
        {
            if (!await db.Database.CanConnectAsync() || await db.LedgerAccounts.AnyAsync())
            {
                return; // unreachable DB or already seeded
            }
        }
        catch (Npgsql.PostgresException)
        {
            return; // schema not migrated yet (e.g. test host start before Migrate) — skip
        }

        db.LedgerAccounts.AddRange(
            new LedgerAccount { Code = Accounts.CashStripe, Name = "Cash — Stripe", Type = AccountType.Asset },
            new LedgerAccount { Code = Accounts.RevenueSales, Name = "Sales revenue", Type = AccountType.Revenue },
            new LedgerAccount { Code = Accounts.RevenueRefunds, Name = "Refunds (contra-revenue)", Type = AccountType.Revenue },
            new LedgerAccount { Code = Accounts.ExpenseStripeFees, Name = "Stripe fees", Type = AccountType.Expense },
            new LedgerAccount { Code = Accounts.ExpenseCostOfGoodsSold, Name = "Cost of goods sold", Type = AccountType.Expense },
            new LedgerAccount { Code = Accounts.LiabilityTaxCollected, Name = "Tax collected", Type = AccountType.Liability },
            new LedgerAccount { Code = Accounts.LiabilitySupplierPayable, Name = "Supplier payables", Type = AccountType.Liability });
        await db.SaveChangesAsync();
        app.Logger.LogInformation("Seeded chart of accounts");
    }
}
