using System.Data;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Per-storefront order numbers under concurrency (order_number_and_edit_races_2026_10_11). The counter row
/// <c>ordering."OrderNumberSequences"</c> is shared by every order of a storefront, and confirmations of different
/// orders run in parallel (the order-status endpoint is partitioned by ORDER id, not storefront). The old
/// allocator read the row and incremented it inside the confirming consumer's REPEATABLE READ outbox transaction,
/// so two confirmations of one storefront collided: <c>40001</c> on the update, <c>23505</c> when both inserted the
/// storefront's first row. <see cref="OrderNumberAllocator"/> now takes the number with one atomic upsert in its
/// own READ COMMITTED transaction, outside the snapshot — gaps allowed, collisions impossible.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public sealed class OrderNumberAllocationTests(Phase3Fixture fixture) : IAsyncLifetime
{
    private static readonly string[] ErrorQueues = ["order-status_error"];

    private static readonly string[] RaceSignatures = ["23505", "40001", "could not serialize", "OrderNumberSequences"];

    private ConsumerRaceProbe _probe = null!;

    public async Task InitializeAsync() =>
        _probe = await ConsumerRaceProbe.StartAsync(fixture.Ordering.Services, fixture.RabbitMqUri, ErrorQueues, RaceSignatures);

    public async Task DisposeAsync() => await _probe.DisposeAsync();

    [Fact]
    public async Task Concurrent_confirmations_in_one_storefront_all_succeed_with_unique_numbers()
    {
        const int Orders = 24;
        var storefrontId = Guid.CreateVersion7(); // a brand-new storefront: the first allocations also race the row insert
        var attempts = new List<Guid>();
        for (var i = 0; i < Orders; i++)
        {
            attempts.Add(await SeedAttemptAsync(storefrontId, $"number{i}@example.com"));
        }

        var bus = fixture.Ordering.Services.GetRequiredService<IBus>();
        var messageIds = attempts.Select(_ => NewId.NextGuid()).ToList();
        await Task.WhenAll(attempts.Select((orderId, i) =>
            bus.Publish(new CheckoutCompleted(orderId), c => c.MessageId = messageIds[i])));
        await _probe.WaitForReceivedAsync(messageIds, "every confirmation processed by Ordering");

        var numbers = await OrderNumbersAsync(attempts);
        Assert.Equal(Orders, numbers.Count); // every attempt became an order
        Assert.Equal(Orders, numbers.Values.Distinct().Count());
        Assert.All(numbers.Values, n => Assert.True(n >= OrderNumberSequence.FirstNumber, $"order number {n} is below the first number"));

        await _probe.AssertNoRaceAsync(); // zero 23505/40001 lines, no faults, no _error growth
    }

    [Theory]
    [InlineData(true, PostgresErrorCodes.SerializationFailure)] // the counter row exists: concurrent updates → 40001
    [InlineData(false, PostgresErrorCodes.UniqueViolation)]     // the storefront's first order: concurrent inserts → 23505
    public async Task Negative_control_the_old_read_then_increment_allocator_collides(bool rowExists, string expectedSqlState)
    {
        const int Racers = 4;
        var storefrontId = Guid.CreateVersion7();
        if (rowExists)
        {
            await SeedSequenceAsync(storefrontId);
        }

        // Every racer reads inside its REPEATABLE READ transaction before any of them writes — exactly the window
        // two parallel consumers had — then increments the way the old OrderStatusConsumer did.
        var allRead = new Barrier(Racers);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, Racers).Select(_ => Task.Run(async () =>
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
            var sequence = await db.OrderNumberSequences.SingleOrDefaultAsync(s => s.StorefrontId == storefrontId);
            allRead.SignalAndWait(TimeSpan.FromSeconds(30));
            try
            {
                if (sequence is null)
                {
                    sequence = new OrderNumberSequence { StorefrontId = storefrontId };
                    db.OrderNumberSequences.Add(sequence);
                }

                sequence.NextNumber++;
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return null;
            }
            catch (Exception ex) when (FindPostgres(ex) is { } pg)
            {
                return pg.SqlState;
            }
        })));

        var failures = outcomes.Where(o => o is not null).ToList();
        Assert.True(failures.Count == Racers - 1, $"expected {Racers - 1} collisions, got [{string.Join(", ", outcomes)}]");
        Assert.All(failures, state => Assert.Equal(expectedSqlState, state));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_atomic_allocator_never_collides_even_inside_open_repeatable_read_snapshots(bool rowExists)
    {
        // The same harness as the negative control, with the allocator in place of read-then-increment.
        const int Racers = 8;
        var storefrontId = Guid.CreateVersion7();
        if (rowExists)
        {
            await SeedSequenceAsync(storefrontId);
        }

        var allRead = new Barrier(Racers);
        var numbers = await Task.WhenAll(Enumerable.Range(0, Racers).Select(_ => Task.Run(async () =>
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            var allocator = scope.ServiceProvider.GetRequiredService<OrderNumberAllocator>();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
            await db.OrderNumberSequences.AnyAsync(s => s.StorefrontId == storefrontId); // take the snapshot
            allRead.SignalAndWait(TimeSpan.FromSeconds(30));
            var number = await allocator.NextAsync(storefrontId, CancellationToken.None);
            await tx.CommitAsync();
            return number;
        })));

        // Unique, and — with nothing rolled back — gapless from the storefront's first number, seeded row or not.
        Assert.Equal(Racers, numbers.Distinct().Count());
        Assert.Equal(Enumerable.Range(0, Racers).Select(i => OrderNumberSequence.FirstNumber + i), numbers.Order());
    }

    private static PostgresException? FindPostgres(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is PostgresException pg)
            {
                return pg;
            }
        }

        return null;
    }

    private async Task SeedSequenceAsync(Guid storefrontId)
    {
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        db.OrderNumberSequences.Add(new OrderNumberSequence { StorefrontId = storefrontId });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedAttemptAsync(Guid storefrontId, string email)
    {
        var id = Guid.CreateVersion7();
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        db.CheckoutAttempts.Add(new CheckoutAttempt
        {
            Id = id,
            TenantId = new Guid("00000000-0000-0000-0000-000000000001"),
            StorefrontId = storefrontId,
            Email = email,
            NetMinor = 1000,
            TaxMinor = 190,
            GrossMinor = 1190,
            Currency = "EUR",
            PaymentIntentId = $"pi_fake_{id:N}",
            ShipName = "Racer",
            ShipLine1 = "1 St",
            ShipCity = "Berlin",
            ShipPostcode = "10115",
            ShipCountry = "DE",
            CreatedAt = DateTimeOffset.UtcNow,
            Lines =
            [
                new CheckoutAttemptLine
                {
                    Id = Guid.CreateVersion7(),
                    CheckoutAttemptId = id,
                    ProductId = Guid.CreateVersion7(),
                    Title = "Race widget",
                    UnitPriceMinor = 1000,
                    Quantity = 1,
                },
            ],
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Dictionary<Guid, long>> OrderNumbersAsync(IEnumerable<Guid> orderIds)
    {
        var ids = orderIds.ToList();
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        return await db.Orders.AsNoTracking().Where(o => ids.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, o => o.PublicOrderNumber);
    }
}
