using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;

namespace ThreeCommerce.Fulfillment.Infrastructure.Consumers;

/// <summary>
/// Fulfils a confirmed order unless it is held (mt4_9). Auto-evaluates an inventory hold; if the
/// order has any active hold it is captured (payload stored) and deferred until released, otherwise
/// it is fulfilled (shipments + warehouse stock + dropship). Idempotent by order.
/// </summary>
public sealed class OrderConfirmedConsumer(FulfilmentProcessor processor, OrderHoldService holds)
    : IConsumer<OrderConfirmed>
{
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
