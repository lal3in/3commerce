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
            if (!await db.Database.CanConnectAsync())
            {
                return; // unreachable DB
            }

            var added = await SeedMissingAsync(db);
            if (added > 0)
            {
                app.Logger.LogInformation("Seeded {Count} chart-of-accounts entries", added);
            }
        }
        catch (Npgsql.PostgresException)
        {
            // schema not migrated yet (e.g. host start before Migrate) — skip
        }
    }

    /// <summary>
    /// Adds only the accounts that are missing, so a partially-populated chart is repaired rather
    /// than skipped. An "any rows → already seeded" check is NOT safe: the PaymentMethodKind
    /// migration backfills the per-provider cash/fee accounts, so on a fresh environment (migrations
    /// always run before the app starts) the chart is non-empty on first boot and an all-or-nothing
    /// seeder would leave revenue.sales / cash.stripe absent — every posting would then fail.
    /// </summary>
    public static async Task<int> SeedMissingAsync(PaymentsDbContext db, CancellationToken ct = default)
    {
        var existing = await db.LedgerAccounts.Select(a => a.Code).ToListAsync(ct);
        var missing = DesiredAccounts().Where(a => !existing.Contains(a.Code)).ToList();
        if (missing.Count == 0)
        {
            return 0;
        }

        db.LedgerAccounts.AddRange(missing);
        await db.SaveChangesAsync(ct);
        return missing.Count;
    }

    /// <summary>The full fixed chart (ADR-0014): the shared accounts plus one cash + fee pair per
    /// known PSP (pay_4), so a sale settles into the provider that actually took the money.</summary>
    public static List<LedgerAccount> DesiredAccounts()
    {
        var accounts = new List<LedgerAccount>
        {
            new() { Code = Accounts.RevenueSales, Name = "Sales revenue", Type = AccountType.Revenue },
            new() { Code = Accounts.RevenueRefunds, Name = "Refunds (contra-revenue)", Type = AccountType.Revenue },
            new() { Code = Accounts.ExpenseCostOfGoodsSold, Name = "Cost of goods sold", Type = AccountType.Expense },
            new() { Code = Accounts.LiabilityTaxCollected, Name = "Tax collected", Type = AccountType.Liability },
            new() { Code = Accounts.LiabilitySupplierPayable, Name = "Supplier payables", Type = AccountType.Liability },
        };

        // Stripe keeps cash.stripe / expense.stripe_fees verbatim.
        accounts.AddRange(LedgerProviders.Known.SelectMany(provider => new[]
        {
            new LedgerAccount { Code = Accounts.CashFor(provider), Name = $"Cash — {ProviderName(provider)}", Type = AccountType.Asset },
            new LedgerAccount { Code = Accounts.FeesFor(provider), Name = $"{ProviderName(provider)} fees", Type = AccountType.Expense },
        }));
        return accounts;
    }

    private static string ProviderName(string provider) => provider switch
    {
        "stripe" => "Stripe",
        "polar" => "Polar",
        "paypal" => "PayPal",
        "afterpay" => "Afterpay",
        _ => provider,
    };
}
