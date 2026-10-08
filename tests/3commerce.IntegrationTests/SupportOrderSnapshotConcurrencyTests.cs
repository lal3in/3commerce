using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Support.Domain;
using ThreeCommerce.Support.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Concurrency guard for Support's order read-copy (BL-8). <c>OrderSnapshotConsumer</c> read-then-inserts one
/// <c>OrderSnapshot</c> per order. Two <see cref="OrderConfirmed"/> messages for one order (distinct message ids,
/// so the inbox cannot dedupe them) consumed in parallel both saw no snapshot inside their REPEATABLE READ outbox
/// transactions, both inserted, and one failed with 23505 on <c>PK_OrderSnapshots</c> until a retry rescued it.
/// The <c>order-snapshot</c> endpoint now partitions by order id, so messages for one order are consumed one at
/// a time (the second sees the committed snapshot and is a no-op) while different orders still run in parallel.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public sealed class SupportOrderSnapshotConcurrencyTests(Phase4Fixture fixture) : IAsyncLifetime
{
    private static readonly ShipToInfo Ship = new("Buyer", "1 St", "Sydney", "2000", "AU");

    // Kebab-case endpoint name of OrderSnapshotConsumer (SetKebabCaseEndpointNameFormatter) + MassTransit's suffix.
    private static readonly string[] ErrorQueues = ["order-snapshot_error"];

    // Signatures of the race in any Support log line or exception.
    private static readonly string[] RaceSignatures =
        ["23505", "40001", "could not serialize", "PK_OrderSnapshots", "OrderSnapshotLines"];

    private ConsumerRaceProbe _probe = null!;

    public async Task InitializeAsync() =>
        _probe = await ConsumerRaceProbe.StartAsync(fixture.Support.Services, fixture.RabbitMqUri, ErrorQueues, RaceSignatures);

    public async Task DisposeAsync() => await _probe.DisposeAsync();

    [Fact]
    public async Task Concurrent_duplicate_OrderConfirmed_for_many_orders_project_one_snapshot_each_without_conflicts()
    {
        const int Orders = 20;
        const int CopiesPerOrder = 3;
        var tenant = Guid.CreateVersion7();
        var orders = Enumerable.Range(0, Orders).Select(i => NewOrder(tenant, lineCount: 1 + (i % 3))).ToList();
        var bus = fixture.Support.Services.GetRequiredService<IBus>();

        // Every copy of every order at once, each with its own message id (not inbox-deduplicable).
        var messageIds = new List<Guid>();
        var publishes = new List<Task>();
        foreach (var order in orders)
        {
            for (var copy = 0; copy < CopiesPerOrder; copy++)
            {
                var id = NewId.NextGuid();
                messageIds.Add(id);
                publishes.Add(bus.Publish(order, c => c.MessageId = id));
            }
        }

        await Task.WhenAll(publishes);
        await _probe.WaitForReceivedAsync(messageIds, "every OrderConfirmed copy processed by Support");

        var snapshots = await ReadAsync(orders.Select(o => o.OrderId));
        foreach (var order in orders)
        {
            Assert.True(snapshots.TryGetValue(order.OrderId, out var s), $"no snapshot for {order.OrderId}");
            Assert.Equal(order.AmountMinor, s.GrossMinor);
            Assert.Equal(order.Currency, s.Currency);
            Assert.Equal(order.Lines.Count, s.Lines.Count);
            Assert.Equal(
                order.Lines.Select(l => (l.ProductId, l.Quantity, l.UnitPriceMinor, l.DiscountMinor)).OrderBy(x => x.ProductId),
                s.Lines.Select(l => (l.ProductId, l.Quantity, l.UnitPriceMinor, l.DiscountMinor)).OrderBy(x => x.ProductId));
        }

        await _probe.AssertNoRaceAsync();
    }

    [Fact]
    public async Task A_redelivered_OrderConfirmed_is_a_no_op_and_the_first_snapshot_stands()
    {
        var tenant = Guid.CreateVersion7();
        var order = NewOrder(tenant, lineCount: 2);
        var bus = fixture.Support.Services.GetRequiredService<IBus>();
        var messageId = NewId.NextGuid();

        await bus.Publish(order, c => c.MessageId = messageId);
        await _probe.WaitForReceivedAsync([messageId], "the first OrderConfirmed processed");

        // The same message again (inbox dedup) and a re-publish under a new id (the snapshot exists: no-op).
        var republishId = NewId.NextGuid();
        await bus.Publish(order, c => c.MessageId = messageId);
        await bus.Publish(order with { AmountMinor = order.AmountMinor + 1 }, c => c.MessageId = republishId);
        await ConsumerRaceProbe.WaitUntilAsync(
            () => Task.FromResult(_probe.ReceivedCount(messageId) >= 2 && _probe.ReceivedCount(republishId) >= 1),
            "the redelivered and re-published OrderConfirmed processed");

        var snapshot = (await ReadAsync([order.OrderId]))[order.OrderId];
        Assert.Equal(order.AmountMinor, snapshot.GrossMinor);
        Assert.Equal(2, snapshot.Lines.Count);

        await _probe.AssertNoRaceAsync();
    }

    private static OrderConfirmed NewOrder(Guid tenant, int lineCount) =>
        new(Guid.CreateVersion7(), tenant, "buyer@example.com", 1000L * lineCount, "EUR", Ship,
            Enumerable.Range(0, lineCount)
                .Select(i => new OrderLineInfo(
                    Guid.CreateVersion7(), null, null, $"Item {i}", i + 1, FulfilmentType.Unassigned, BillingMode.OneTime, 500, i * 10))
                .ToList());

    private async Task<Dictionary<Guid, OrderSnapshot>> ReadAsync(IEnumerable<Guid> orderIds)
    {
        var ids = orderIds.ToList();
        using var scope = fixture.Support.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SupportDbContext>();
        return await db.OrderSnapshots.AsNoTracking().Include(o => o.Lines)
            .Where(o => ids.Contains(o.OrderId))
            .ToDictionaryAsync(o => o.OrderId);
    }
}
