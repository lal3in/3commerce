using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using ThreeCommerce.Payments.Api;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// NFR-1: the ledger is balanced and append-only at the database level. These run the real
/// PaymentsLedger migration (triggers included) against a throwaway Postgres.
/// </summary>
[Trait("Category", "Integration")]
public class LedgerInvariantTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();

    public async Task InitializeAsync() => await _postgres.StartAsync();
    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    private PaymentsDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        return new PaymentsDbContext(options);
    }

    [Fact]
    public async Task Balanced_entry_commits_and_trial_balance_is_zero()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();

        var now = DateTimeOffset.UtcNow;
        db.JournalEntries.Add(Ledger.Sale(Guid.CreateVersion7(), 11900, 1900, 350, "EUR", now));
        db.JournalEntries.Add(Ledger.Refund(Guid.CreateVersion7(), Guid.CreateVersion7(), 5950, 950, "EUR", now));
        await db.SaveChangesAsync();

        var debits = await db.JournalLines.SumAsync(l => l.DebitMinor);
        var credits = await db.JournalLines.SumAsync(l => l.CreditMinor);
        Assert.Equal(debits, credits); // trial balance zero
    }

    [Fact]
    public async Task Provider_scoped_postings_commit_and_keep_the_trial_balance_zero()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();

        // Two PSPs settling side by side: each keeps its own cash/fee accounts, and the whole
        // book still nets to zero (attribution must never cost us NFR-1).
        var now = DateTimeOffset.UtcNow;
        var stripeOrder = Guid.CreateVersion7();
        var paypalOrder = Guid.CreateVersion7();
        db.JournalEntries.Add(Ledger.Sale(stripeOrder, 11_900, 1_900, 350, "EUR", now, PaymentMethodKind.GooglePay, "stripe"));
        db.JournalEntries.Add(Ledger.Sale(paypalOrder, 8_000, 0, 200, "EUR", now, PaymentMethodKind.PayPal, "paypal"));
        await db.SaveChangesAsync();

        Assert.Equal(
            await db.JournalLines.SumAsync(l => l.DebitMinor),
            await db.JournalLines.SumAsync(l => l.CreditMinor));

        var postedCodes = await db.JournalLines.Select(l => l.AccountCode).Distinct().ToListAsync();
        Assert.Contains("cash.paypal", postedCodes);
        Assert.Contains("expense.paypal_fees", postedCodes);
        Assert.Contains(Accounts.CashStripe, postedCodes);

        // The PaymentMethodKind migration backfills the pay_4 provider accounts into the chart, so
        // an already-seeded database gets them without a reseed.
        var chart = await db.LedgerAccounts.Select(a => a.Code).ToListAsync();
        Assert.Contains("cash.paypal", chart);
        Assert.Contains("cash.polar", chart);
        Assert.Contains("cash.afterpay", chart);
        Assert.Contains("expense.paypal_fees", chart);

        var descriptions = await db.JournalEntries.Select(e => e.Description).ToListAsync();
        Assert.Contains(descriptions, d => d.Contains("via GooglePay"));
        Assert.Contains(descriptions, d => d.Contains("via PayPal"));
    }

    [Fact]
    public async Task Seeder_repairs_a_chart_the_migration_backfill_already_populated()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();

        // A freshly-migrated database does NOT have an empty chart: the PaymentMethodKind migration
        // backfills the per-provider cash/fee accounts. Migrations always run before the app starts
        // (dev-up, the Helm migrator job, CI), so this IS the first-boot state everywhere — an
        // "any rows means seeded" guard would skip here and leave the base accounts missing, and
        // every sale posting would fail against an account that does not exist.
        var afterMigrate = await db.LedgerAccounts.Select(a => a.Code).ToListAsync();
        Assert.NotEmpty(afterMigrate);
        Assert.DoesNotContain(Accounts.RevenueSales, afterMigrate);

        Assert.True(await ChartOfAccountsSeeder.SeedMissingAsync(db) > 0);

        var chart = await db.LedgerAccounts.Select(a => a.Code).ToListAsync();
        Assert.Contains(Accounts.RevenueSales, chart);
        Assert.Contains(Accounts.CashStripe, chart);
        Assert.Contains(Accounts.LiabilityTaxCollected, chart);
        Assert.Contains("cash.paypal", chart);                    // backfilled rows survive…
        Assert.Equal(chart.Count, chart.Distinct().Count());      // …and are not duplicated
        Assert.Equal(0, await ChartOfAccountsSeeder.SeedMissingAsync(db)); // idempotent second pass

        // Proof it is actually usable: a sale now posts and balances.
        db.JournalEntries.Add(Ledger.Sale(Guid.CreateVersion7(), 11900, 1900, 350, "EUR", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        Assert.Equal(
            await db.JournalLines.SumAsync(l => l.DebitMinor),
            await db.JournalLines.SumAsync(l => l.CreditMinor));
    }

    [Fact]
    public async Task Unbalanced_entry_is_rejected_at_commit()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();

        var entry = new JournalEntry
        {
            Id = Guid.CreateVersion7(),
            Description = "bad",
            Reference = "x",
            Currency = "EUR",
            CreatedAt = DateTimeOffset.UtcNow,
            Lines =
            [
                new JournalLine { Id = Guid.CreateVersion7(), AccountCode = Accounts.CashStripe, DebitMinor = 100, CreditMinor = 0 },
                new JournalLine { Id = Guid.CreateVersion7(), AccountCode = Accounts.RevenueSales, DebitMinor = 0, CreditMinor = 99 },
            ],
        };
        db.JournalEntries.Add(entry);

        // The balance trigger is DEFERRED to commit, so it raises a PostgresException
        // directly (not wrapped by EF's per-command DbUpdateException).
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());
        var pg = ex as PostgresException ?? ex.InnerException as PostgresException;
        Assert.NotNull(pg);
        Assert.Contains("Unbalanced", pg!.MessageText);
    }

    [Fact]
    public async Task Ledger_rows_cannot_be_updated_or_deleted()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();

        var entry = Ledger.Sale(Guid.CreateVersion7(), 1000, 0, 0, "EUR", DateTimeOffset.UtcNow);
        db.JournalEntries.Add(entry);
        await db.SaveChangesAsync();

        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();

        await using var update = new NpgsqlCommand("UPDATE payments.\"JournalLines\" SET \"DebitMinor\" = 1 WHERE TRUE", conn);
        var updateEx = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
        Assert.Contains("append-only", updateEx.MessageText);

        await using var delete = new NpgsqlCommand("DELETE FROM payments.\"JournalEntries\" WHERE TRUE", conn);
        var deleteEx = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
        Assert.Contains("append-only", deleteEx.MessageText);
    }
}
