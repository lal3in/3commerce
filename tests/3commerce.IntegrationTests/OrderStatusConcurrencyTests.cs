using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Concurrency guard for Ordering's order-status projection. <c>OrderStatusConsumer</c> applies five events
/// (CheckoutCompleted, OrderCancelled, RefundCompleted, PaymentDisputed, PaymentChargedBack) to ONE row,
/// <c>ordering."Orders"</c>. Several events for one order arrive together (a dispute that goes straight to lost
/// publishes PaymentDisputed and PaymentChargedBack back to back; partial refunds follow each other). Consumed in
/// parallel, their REPEATABLE READ outbox transactions all updated the same row and all but one failed with 40001
/// until a retry rescued them. The endpoint now partitions every message type by order id through one shared
/// partitioner (<c>OrderStatusConsumer.PartitionByOrder</c>), so an order's events apply one at a time.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public sealed class OrderStatusConcurrencyTests(Phase3Fixture fixture) : IAsyncLifetime
{
    // Kebab-case endpoint name of OrderStatusConsumer (ADR-0060) + MassTransit's suffix.
    private static readonly string[] ErrorQueues = ["order-status_error"];

    // Signatures of the race in any Ordering log line or exception.
    private static readonly string[] RaceSignatures = ["23505", "40001", "could not serialize"];

    private ConsumerRaceProbe _probe = null!;

    public async Task InitializeAsync() =>
        _probe = await ConsumerRaceProbe.StartAsync(fixture.Ordering.Services, fixture.RabbitMqUri, ErrorQueues, RaceSignatures);

    public async Task DisposeAsync() => await _probe.DisposeAsync();

    [Fact]
    public async Task Concurrent_status_events_for_many_orders_all_apply_without_serialization_failures()
    {
        const int Orders = 20;
        var orders = new List<Guid>();
        for (var i = 0; i < Orders; i++)
        {
            orders.Add(await fixture.SeedGuestOrderAsync($"race{i}@example.com"));
        }

        var bus = fixture.Ordering.Services.GetRequiredService<IBus>();
        var messageIds = new List<Guid>();
        var publishes = new List<Task>();
        foreach (var orderId in orders)
        {
            // A dispute opened and lost in one window, plus two partial refunds: every event writes the same
            // Orders row. They commute (each sets a flag), so the end state does not depend on their order.
            var intent = $"pi_fake_{orderId:N}";
            object[] events =
            [
                new PaymentDisputed(orderId, intent, 1190),
                new PaymentChargedBack(orderId, intent, 1190),
                new RefundCompleted(Guid.CreateVersion7(), orderId, 300),
                new RefundCompleted(Guid.CreateVersion7(), orderId, 200),
            ];
            foreach (var message in events)
            {
                var id = NewId.NextGuid();
                messageIds.Add(id);
                publishes.Add(bus.Publish(message, message.GetType(), c => c.MessageId = id));
            }
        }

        await Task.WhenAll(publishes);
        await _probe.WaitForReceivedAsync(messageIds, "every order-status event processed by Ordering");

        var rows = await OrdersAsync(orders);
        foreach (var orderId in orders)
        {
            var order = rows[orderId];
            Assert.True(order.Disputed, $"order {orderId} lost its Disputed flag");
            Assert.True(order.PartiallyRefunded, $"order {orderId} lost its PartiallyRefunded flag");
            Assert.Equal(OrderStatus.Confirmed, order.Status); // partial refunds leave the order standing
        }

        await _probe.AssertNoRaceAsync();
    }

    [Fact]
    public async Task An_orders_events_apply_in_queue_order_so_a_full_refund_after_a_partial_one_lands_refunded()
    {
        // Same-key ordering matters here: a full refund moves Confirmed → Refunded, after which a partial refund is
        // a no-op. Published in order (partial, then full) for several orders at once, every order must end
        // Refunded AND flagged PartiallyRefunded — a retried, reordered partial would lose the flag.
        const int Orders = 10;
        var orders = new List<Guid>();
        for (var i = 0; i < Orders; i++)
        {
            orders.Add(await fixture.SeedGuestOrderAsync($"order{i}@example.com"));
        }

        var bus = fixture.Ordering.Services.GetRequiredService<IBus>();
        var messageIds = new List<Guid>();
        await Task.WhenAll(orders.Select(async orderId =>
        {
            Guid partialId = NewId.NextGuid(), fullId = NewId.NextGuid();
            lock (messageIds)
            {
                messageIds.Add(partialId);
                messageIds.Add(fullId);
            }

            await bus.Publish(new RefundCompleted(Guid.CreateVersion7(), orderId, 500), c => c.MessageId = partialId);
            await bus.Publish(new RefundCompleted(Guid.CreateVersion7(), orderId, 690, FullyRefunded: true), c => c.MessageId = fullId);
        }));

        await _probe.WaitForReceivedAsync(messageIds, "every refund event processed by Ordering");

        var rows = await OrdersAsync(orders);
        foreach (var orderId in orders)
        {
            Assert.Equal(OrderStatus.Refunded, rows[orderId].Status);
            Assert.True(rows[orderId].PartiallyRefunded, $"order {orderId}: the partial refund was applied after the full one");
        }

        await _probe.AssertNoRaceAsync();
    }

    [Fact]
    public async Task A_redelivered_status_event_is_a_no_op()
    {
        var orderId = await fixture.SeedGuestOrderAsync("redelivery@example.com");
        var bus = fixture.Ordering.Services.GetRequiredService<IBus>();
        var messageId = NewId.NextGuid();
        var refund = new RefundCompleted(Guid.CreateVersion7(), orderId, 1190, FullyRefunded: true);

        await bus.Publish(refund, c => c.MessageId = messageId);
        await _probe.WaitForReceivedAsync([messageId], "the refund processed");
        await bus.Publish(refund, c => c.MessageId = messageId);
        await ConsumerRaceProbe.WaitUntilAsync(
            () => Task.FromResult(_probe.ReceivedCount(messageId) >= 2), "the redelivered refund processed");

        var order = (await OrdersAsync([orderId]))[orderId];
        Assert.Equal(OrderStatus.Refunded, order.Status);
        Assert.False(order.PartiallyRefunded);
        await _probe.AssertNoRaceAsync();
    }

    private async Task<Dictionary<Guid, Order>> OrdersAsync(IEnumerable<Guid> orderIds)
    {
        var ids = orderIds.ToList();
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        return await db.Orders.AsNoTracking().Where(o => ids.Contains(o.Id)).ToDictionaryAsync(o => o.Id);
    }
}
