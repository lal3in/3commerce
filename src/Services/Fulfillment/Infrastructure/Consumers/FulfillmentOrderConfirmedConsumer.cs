using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;

namespace ThreeCommerce.Fulfillment.Infrastructure.Consumers;

/// <summary>
/// Fulfils a confirmed order unless it is held (mt4_9). Auto-evaluates an inventory hold; if the
/// order has any active hold it is captured (payload stored) and deferred until released, otherwise
/// it is fulfilled (shipments + warehouse stock + dropship). Idempotent by order.
/// </summary>
public sealed class FulfillmentOrderConfirmedConsumer(FulfilmentProcessor processor, OrderHoldService holds)
    : IConsumer<OrderConfirmed>
{
    /// <summary>How many orders the endpoint fulfils in parallel (one partition each).</summary>
    public const int Partitions = 16;

    /// <summary>
    /// Consumes <see cref="OrderConfirmed"/> one at a time PER ORDER on this consumer's endpoint, while different
    /// orders still run in parallel. Wire it with <c>.Endpoint(e => e.AddConfigureEndpointCallback(PartitionByOrder))</c>.
    /// <para>
    /// The consumer reads, then writes (an order is fulfilled once: "no shipment yet" → reserve stock, forward
    /// dropship lines, insert one shipment per fulfilment source; or "held" → capture one <c>HeldOrder</c>). Two
    /// <see cref="OrderConfirmed"/> for one order with different message ids (which the inbox cannot dedupe) used
    /// to run concurrently. Each ran in the EF outbox's REPEATABLE READ transaction, whose snapshot is taken at the
    /// inbox lock, so both saw no shipment, both did the work, and one failed with 23505 on
    /// <c>IX_Shipments_OrderId_FulfillmentSource</c> (or <c>IX_HeldOrders_OrderId</c>) and was rolled back until a
    /// retry found the committed shipments and returned. A second shipment for the same order and source is never
    /// legitimate, so the duplicate must be a no-op, not an error.
    /// </para>
    /// <para>
    /// The partitioner sits on the endpoint's message pipe, OUTSIDE the per-consumer outbox filter, so the second
    /// message for an order only opens its transaction after the first has committed; it then sees the shipments
    /// (or the held order) and returns. It is per process: replicas competing on the queue can still race, and the
    /// unique indexes plus the retry policy cover that rarer case with the same end state.
    /// </para>
    /// </summary>
    public static void PartitionByOrder(IReceiveEndpointConfigurator endpoint) =>
        endpoint.UsePartitioner<OrderConfirmed>(endpoint.CreatePartitioner(Partitions), m => m.Message.OrderId);

    public async Task Consume(ConsumeContext<OrderConfirmed> context)
    {
        var m = context.Message;
        var ct = context.CancellationToken;

        // Already captured as held → wait for release (idempotent on redelivery).
        if (await holds.HeldOrderExistsAsync(m.OrderId, ct))
        {
            return;
        }

        await holds.EvaluateInventoryHoldAsync(m, ct);

        if (await holds.HasActiveHoldAsync(m.TenantId, m.OrderId, ct))
        {
            await holds.CaptureHeldOrderAsync(m, ct);
            return;
        }

        await processor.FulfilAsync(m, context, ct);
    }
}
