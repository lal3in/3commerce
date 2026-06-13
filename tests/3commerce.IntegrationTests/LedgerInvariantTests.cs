using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
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

        await using var update = new NpgsqlCommand("UPDATE \"JournalLines\" SET \"DebitMinor\" = 1 WHERE TRUE", conn);
        var updateEx = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
        Assert.Contains("append-only", updateEx.MessageText);

        await using var delete = new NpgsqlCommand("DELETE FROM \"JournalEntries\" WHERE TRUE", conn);
        var deleteEx = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
        Assert.Contains("append-only", deleteEx.MessageText);
    }
}
