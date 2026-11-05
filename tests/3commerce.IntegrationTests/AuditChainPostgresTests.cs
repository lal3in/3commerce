using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;
using ThreeCommerce.Entity.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// audit_chain_timestamp_precision (ADR-0029 amendment 2026-11-04) on a real Postgres: the local audit chain (Entity's
/// <c>entity."AuditEntries"</c> through <see cref="EfAuditStore{TContext}"/>) must verify after a timestamptz round
/// trip. Postgres keeps microseconds and Npgsql drops the seventh fractional digit, while a Linux clock fills it — so
/// before the fix ~9 in 10 entries came back "tampered". The clock is pinned to …54.1755249 so this is deterministic on
/// macOS too. Also pins the assumption the legacy verification rests on: Npgsql TRUNCATES (…249 → …240), never rounds.
/// </summary>
[Trait("Category", "Integration")]
public class AuditChainPostgresTests : IAsyncLifetime
{
    private static readonly DateTimeOffset LinuxNow = new DateTimeOffset(2026, 11, 4, 9, 12, 54, TimeSpan.Zero).AddTicks(1_755_249);
    private static readonly DateTimeOffset StoredNow = new DateTimeOffset(2026, 11, 4, 9, 12, 54, TimeSpan.Zero).AddTicks(1_755_240);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18").Build();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private EntityDbContext NewContext() =>
        new(new DbContextOptionsBuilder<EntityDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options);

    private AuditDraft Draft(string action) =>
        new(_tenant, action, "SupplierChangeRequest", "r1", AuditOutcome.Success, Guid.NewGuid(), "admin", "BankAccount");

    private async Task<List<AuditEntry>> RecordAsync(DateTimeOffset now, params string[] actions)
    {
        await using var db = NewContext();
        var recorder = new AuditRecorder(new EfAuditStore<EntityDbContext>(db), new FixedClock(now));
        var entries = new List<AuditEntry>();
        foreach (var action in actions)
        {
            entries.Add(await recorder.RecordAsync(Draft(action), default));
            await db.SaveChangesAsync(); // LastAsync reads the committed previous entry, as the endpoints do per request
        }

        return entries;
    }

    private async Task<(AuditVerification Result, List<AuditEntry> Chain)> ReloadAndVerifyAsync()
    {
        await using var db = NewContext();
        var store = new EfAuditStore<EntityDbContext>(db);
        var chain = await store.ChainAsync(_tenant, default);
        return (await new AuditRecorder(store, TimeProvider.System).VerifyAsync(_tenant, default), chain);
    }

    [Fact]
    public async Task A_chain_recorded_at_a_sub_microsecond_tick_verifies_after_the_Postgres_round_trip()
    {
        var recorded = await RecordAsync(LinuxNow, "supplier.change_request.approved", "supplier.change_request.rejected", "field_reveal.bsb");

        var (result, chain) = await ReloadAndVerifyAsync();

        Assert.True(result.Intact, $"broken at {result.FirstBrokenSequence}");
        Assert.Equal(0, result.LegacyPrecisionEntries);
        Assert.Equal(3, chain.Count);
        Assert.All(chain, e => Assert.Equal(StoredNow, e.OccurredAt));
        Assert.Equal(recorded.Select(e => (e.OccurredAt, e.Hash)), chain.Select(e => (e.OccurredAt, e.Hash))); // memory == DB
    }

    [Fact]
    public async Task Rows_written_before_the_fix_verify_from_their_truncated_time_and_new_entries_chain_onto_them()
    {
        // Rows exactly as the pre-fix code wrote them: hashed over the untruncated 100 ns time.
        var legacy = new List<AuditEntry>();
        foreach (var subTicks in new[] { 9, 0, 4 })
        {
            var at = StoredNow.AddSeconds(legacy.Count).AddTicks(subTicks);
            var entry = new AuditEntry
            {
                Id = Guid.CreateVersion7(),
                TenantId = _tenant,
                Sequence = legacy.Count + 1,
                OccurredAt = at,
                ActorId = Guid.NewGuid(),
                ActorRole = "admin",
                Action = $"legacy.{legacy.Count + 1}",
                ResourceType = "SupplierChangeRequest",
                ResourceId = "r1",
                Outcome = AuditOutcome.Success,
                Summary = "BankAccount",
                PrevHash = legacy.LastOrDefault()?.Hash ?? AuditEntry.Genesis,
                Hash = string.Empty,
            };
            legacy.Add(entry with { Hash = entry.ComputeHash() });
        }

        await using (var db = NewContext())
        {
            db.Set<AuditEntry>().AddRange(legacy);
            await db.SaveChangesAsync();
        }

        var (before, stored) = await ReloadAndVerifyAsync();
        // Npgsql truncated (…249 → …240, …244 → …240), it did not round — the legacy search's window depends on it.
        Assert.Equal(StoredNow, stored[0].OccurredAt);
        Assert.Equal(StoredNow.AddSeconds(2), stored[2].OccurredAt);
        Assert.NotEqual(legacy[0].Hash, stored[0].ComputeHash());
        Assert.True(before.Intact, $"broken at {before.FirstBrokenSequence}");
        Assert.Equal(2, before.LegacyPrecisionEntries); // the sub-tick-0 row round-trips and verifies exactly

        await RecordAsync(LinuxNow.AddMinutes(1), "supplier.change_request.approved");

        var (after, chain) = await ReloadAndVerifyAsync();
        Assert.True(after.Intact, $"broken at {after.FirstBrokenSequence}");
        Assert.Equal(2, after.LegacyPrecisionEntries);
        Assert.Equal(4, chain.Count);
        Assert.Equal(legacy[^1].Hash, chain[^1].PrevHash);
    }

    [Fact]
    public async Task Moving_a_stored_time_by_one_microsecond_in_the_database_breaks_the_chain()
    {
        await RecordAsync(LinuxNow, "a", "b", "c");

        await using (var db = NewContext())
        {
            var moved = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE entity."AuditEntries" SET "OccurredAt" = "OccurredAt" + interval '1 microsecond'
                WHERE "TenantId" = {_tenant} AND "Sequence" = 2
                """);
            Assert.Equal(1, moved);
        }

        var (result, _) = await ReloadAndVerifyAsync();
        Assert.False(result.Intact);
        Assert.Equal(2, result.FirstBrokenSequence);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
