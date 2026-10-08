using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Fulfillment.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Concurrency guard for Fulfillment's order intake (mt4_2/mt4_9). <c>FulfillmentOrderConfirmedConsumer</c> fulfils
/// an order once: it checks for existing shipments (or a captured held order), then writes. Two
/// <see cref="OrderConfirmed"/> for one order with distinct message ids (not inbox-deduplicable) consumed in
/// parallel both passed the check inside their REPEATABLE READ outbox transactions, and one failed with 23505 on
/// <c>IX_Shipments_OrderId_FulfillmentSource</c> (or <c>IX_HeldOrders_OrderId</c>) until a retry rescued it. The
/// endpoint now partitions by order id (<c>FulfillmentOrderConfirmedConsumer.PartitionByOrder</c>), so a duplicate
/// is a clean no-op while different orders still run in parallel.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public sealed class FulfillmentShipmentConcurrencyTests(Phase4Fixture fixture) : IAsyncLifetime
{
    private static readonly ShipToInfo Ship = new("Buyer", "1 St", "Sydney", "2000", "AU");

    // Kebab-case endpoint name of FulfillmentOrderConfirmedConsumer (ADR-0060) + MassTransit's suffix.
    private static readonly string[] ErrorQueues = ["fulfillment-order-confirmed_error"];

    // Signatures of the race in any Fulfillment log line or exception.
    private static readonly string[] RaceSignatures =
        ["23505", "40001", "could not serialize", "IX_Shipments_OrderId_FulfillmentSource", "IX_HeldOrders_OrderId"];

    private ConsumerRaceProbe _probe = null!;

    public async Task InitializeAsync() =>
        _probe = await ConsumerRaceProbe.StartAsync(fixture.Fulfillment.Services, fixture.RabbitMqUri, ErrorQueues, RaceSignatures);

    public async Task DisposeAsync() => await _probe.DisposeAsync();

    [Fact]
    public async Task Concurrent_duplicate_OrderConfirmed_for_many_orders_create_one_shipment_per_source_without_conflicts()
    {
        const int Orders = 20;
        const int CopiesPerOrder = 3;
        var tenant = Guid.CreateVersion7();
        // Two physical sources per order (Unassigned + a supplier-less Dropship line, which ships but forwards
        // nothing), so each order expects exactly two shipments.
        var orders = Enumerable.Range(0, Orders).Select(_ => new OrderConfirmed(
            Guid.CreateVersion7(), tenant, "buyer@example.com", 3000, "EUR", Ship,
            [
                new OrderLineInfo(Guid.CreateVersion7(), null, null, "Loose A", 1, FulfilmentType.Unassigned, BillingMode.OneTime, 1000),
                new OrderLineInfo(Guid.CreateVersion7(), null, null, "Loose B", 2, FulfilmentType.Unassigned, BillingMode.OneTime, 500),
                new OrderLineInfo(Guid.CreateVersion7(), null, null, "Drop C", 1, FulfilmentType.Dropship, BillingMode.OneTime, 1000),
            ])).ToList();

        var messageIds = await PublishCopiesAsync(orders, CopiesPerOrder);
        await _probe.WaitForReceivedAsync(messageIds, "every OrderConfirmed copy processed by Fulfillment");

        var shipments = await ShipmentsAsync(orders.Select(o => o.OrderId));
        foreach (var order in orders)
        {
            var mine = shipments.Where(s => s.OrderId == order.OrderId).ToList();
            Assert.Equal(2, mine.Count);
            Assert.Contains(mine, s => s.FulfillmentSource == nameof(FulfilmentType.Unassigned) && s.Lines.Count == 2);
            Assert.Contains(mine, s => s.FulfillmentSource == nameof(FulfilmentType.Dropship) && s.Lines.Count == 1);
        }

        await _probe.AssertNoRaceAsync();
    }

    [Fact]
    public async Task Concurrent_duplicate_OrderConfirmed_for_held_orders_capture_each_order_once()
    {
        // A warehouse line with no stock auto-holds the order (mt4_9): it is captured as ONE HeldOrder with ONE
        // active inventory hold, however many copies of the event race in, and nothing ships.
        const int Orders = 10;
        const int CopiesPerOrder = 3;
        var tenant = Guid.CreateVersion7();
        var orders = Enumerable.Range(0, Orders).Select(_ => new OrderConfirmed(
            Guid.CreateVersion7(), tenant, "buyer@example.com", 2000, "EUR", Ship,
            [new OrderLineInfo(Guid.CreateVersion7(), null, null, "Out of stock", 1, FulfilmentType.Warehouse, BillingMode.OneTime, 2000)]))
            .ToList();

        var messageIds = await PublishCopiesAsync(orders, CopiesPerOrder);
        await _probe.WaitForReceivedAsync(messageIds, "every held OrderConfirmed copy processed by Fulfillment");

        var ids = orders.Select(o => o.OrderId).ToList();
        using var scope = fixture.Fulfillment.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FulfillmentDbContext>();
        var held = await db.HeldOrders.AsNoTracking().Where(h => ids.Contains(h.OrderId)).ToListAsync();
        var holds = await db.OrderHolds.AsNoTracking().Where(h => ids.Contains(h.OrderId)).ToListAsync();
        foreach (var id in ids)
        {
            Assert.Single(held, h => h.OrderId == id);
            Assert.Single(holds, h => h.OrderId == id && h.Reason == HoldReason.Inventory && h.Status == HoldStatus.Active);
        }

        Assert.Empty(await ShipmentsAsync(ids));
        await _probe.AssertNoRaceAsync();
    }

    [Fact]
    public async Task A_redelivered_or_republished_OrderConfirmed_is_a_no_op()
    {
        var order = new OrderConfirmed(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "buyer@example.com", 1000, "EUR", Ship,
            [new OrderLineInfo(Guid.CreateVersion7(), null, null, "Item", 1, FulfilmentType.Unassigned, BillingMode.OneTime, 1000)]);
        var bus = fixture.Fulfillment.Services.GetRequiredService<IBus>();
        var messageId = NewId.NextGuid();

        await bus.Publish(order, c => c.MessageId = messageId);
        await _probe.WaitForReceivedAsync([messageId], "the first OrderConfirmed processed");

        // The same message again (inbox dedup) and a re-publish under a new id (already fulfilled: no-op).
        var republishId = NewId.NextGuid();
        await bus.Publish(order, c => c.MessageId = messageId);
        await bus.Publish(order, c => c.MessageId = republishId);
        await ConsumerRaceProbe.WaitUntilAsync(
            () => Task.FromResult(_probe.ReceivedCount(messageId) >= 2 && _probe.ReceivedCount(republishId) >= 1),
            "the redelivered and re-published OrderConfirmed processed");

        var shipment = Assert.Single(await ShipmentsAsync([order.OrderId]));
        Assert.Single(shipment.Lines);
        await _probe.AssertNoRaceAsync();
    }

    /// <summary>Publishes every copy of every order at once, each with its own message id; returns the ids.</summary>
    private async Task<List<Guid>> PublishCopiesAsync(IEnumerable<OrderConfirmed> orders, int copies)
    {
        var bus = fixture.Fulfillment.Services.GetRequiredService<IBus>();
        var messageIds = new List<Guid>();
        var publishes = new List<Task>();
        foreach (var order in orders)
        {
            for (var copy = 0; copy < copies; copy++)
            {
                var id = NewId.NextGuid();
                messageIds.Add(id);
                publishes.Add(bus.Publish(order, c => c.MessageId = id));
            }
        }

        await Task.WhenAll(publishes);
        return messageIds;
    }

    private async Task<List<Shipment>> ShipmentsAsync(IEnumerable<Guid> orderIds)
    {
        var ids = orderIds.ToList();
        using var scope = fixture.Fulfillment.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FulfillmentDbContext>();
        return await db.Shipments.AsNoTracking().Include(s => s.Lines).Where(s => ids.Contains(s.OrderId)).ToListAsync();
    }
}
