using System.Data;
using System.Reflection;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Unit lane: every service's bus-outbox DELIVERY runs READ COMMITTED while its consumers keep REPEATABLE READ.
/// MassTransit's <c>BusOutboxDeliveryService</c> runs up to six delivery transactions at once after a busy pass (and
/// each replica runs its own). Under REPEATABLE READ they collided on the outbox lock with <c>40001 … concurrent
/// update</c>, the delivery backed off 3–30 s and every pending message of the service was late
/// (StorefrontReadinessCrossServiceTests and AuditTimelineProjectionTests timed out, 2026-10-15/16).
/// </summary>
public sealed class OutboxDeliveryTests
{
    [Fact]
    public async Task AddServiceBus_runs_outbox_delivery_read_committed_and_leaves_consumers_repeatable_read()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddServiceBus<OutboxDeliveryDbContext>(new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider();

        var deliveries = provider.GetServices<IHostedService>().OfType<BusOutboxDeliveryService<OutboxDeliveryDbContext>>().ToList();
        var delivery = Assert.Single(deliveries); // replaced, not added next to MassTransit's own
        var isolation = typeof(BusOutboxDeliveryService<OutboxDeliveryDbContext>)
            .GetField("_isolationLevel", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(delivery);
        Assert.Equal(IsolationLevel.ReadCommitted, isolation);
        Assert.Equal(MassTransitExtensions.OutboxDeliveryIsolation, isolation);

        // The consumer outbox (inbox lock + consumer writes) is untouched: ADR-0007's race rules assume it.
        var consumerOptions = provider.GetRequiredService<IOptions<EntityFrameworkOutboxOptions<OutboxDeliveryDbContext>>>().Value;
        Assert.Equal(IsolationLevel.RepeatableRead, consumerOptions.IsolationLevel);
    }

    private sealed class OutboxDeliveryDbContext(DbContextOptions<OutboxDeliveryDbContext> options) : DbContext(options);
}

/// <summary>
/// The mechanism on a real Postgres, with MassTransit's own lock statement shape: a delivery transaction that takes
/// its snapshot, then meets an outbox row another delivery updated and committed, fails with 40001 under REPEATABLE
/// READ — and under READ COMMITTED skips past it and takes the next batch.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OutboxDeliveryIsolationIntegrationTests : IAsyncLifetime
{
    private const string LockOldest =
        "SELECT \"OutboxId\" FROM outbox_state ORDER BY \"Created\" LIMIT 1 FOR UPDATE SKIP LOCKED";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18").Build();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await ExecAsync("""
            CREATE TABLE outbox_state ("OutboxId" uuid PRIMARY KEY, "Created" timestamptz NOT NULL, "LockId" uuid NULL);
            """);
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task Repeatable_read_delivery_fails_with_40001_and_read_committed_skips_the_changed_batch()
    {
        var error = await RaceAsync(IsolationLevel.RepeatableRead);
        Assert.Equal(PostgresErrorCodes.SerializationFailure, Assert.IsType<PostgresException>(error.Exception).SqlState);

        var ok = await RaceAsync(IsolationLevel.ReadCommitted);
        Assert.Null(ok.Exception);
        Assert.Equal(ok.Second, ok.Locked); // the changed oldest batch is gone; it took the next one
    }

    // Two batches. Delivery B opens its transaction and takes its snapshot; delivery A then finishes the oldest batch
    // (deletes it) and commits; B now runs MassTransit's lock statement.
    private async Task<(Exception? Exception, Guid Second, Guid? Locked)> RaceAsync(IsolationLevel isolation)
    {
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        await ExecAsync($"""
            TRUNCATE outbox_state;
            INSERT INTO outbox_state VALUES ('{first}', now() - interval '1 second', NULL), ('{second}', now(), NULL);
            """);

        await using var b = new NpgsqlConnection(_postgres.GetConnectionString());
        await b.OpenAsync();
        await using var tx = await b.BeginTransactionAsync(isolation);
        await using (var snapshot = new NpgsqlCommand("SELECT count(*) FROM outbox_state", b, tx))
        {
            await snapshot.ExecuteScalarAsync();
        }

        await ExecAsync($"UPDATE outbox_state SET \"LockId\" = gen_random_uuid() WHERE \"OutboxId\" = '{first}'; DELETE FROM outbox_state WHERE \"OutboxId\" = '{first}';");

        try
        {
            await using var lockOldest = new NpgsqlCommand(LockOldest, b, tx);
            var locked = (Guid?)await lockOldest.ExecuteScalarAsync();
            await tx.CommitAsync();
            return (null, second, locked);
        }
        catch (PostgresException ex)
        {
            return (ex, second, null);
        }
    }

    private async Task ExecAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
