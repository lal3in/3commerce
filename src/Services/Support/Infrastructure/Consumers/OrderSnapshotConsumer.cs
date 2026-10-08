using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.Support.Domain;

namespace ThreeCommerce.Support.Infrastructure.Consumers;

/// <summary>Keeps a local order read-copy so RMA amounts are derived server-side (BL-8).</summary>
public sealed class OrderSnapshotConsumer(SupportDbContext db) : IConsumer<OrderConfirmed>
{
    /// <summary>How many orders the endpoint consumes in parallel (one partition each).</summary>
    public const int Partitions = 16;

    /// <summary>
    /// Consumes <see cref="OrderConfirmed"/> one at a time PER ORDER on this consumer's endpoint, while different
    /// orders still run in parallel. Wire it with <c>.Endpoint(e => e.AddConfigureEndpointCallback(PartitionByOrder))</c>.
    /// <para>
    /// Two <see cref="OrderConfirmed"/> for one order with different message ids (which the inbox cannot dedupe)
    /// used to run concurrently. Each ran in the EF outbox's REPEATABLE READ transaction, whose snapshot is taken
    /// at the inbox lock, so both saw no snapshot, both inserted, and one failed with 23505 on
    /// <c>PK_OrderSnapshots</c> until a retry rescued it. <c>INSERT … ON CONFLICT DO NOTHING</c> does not help
    /// under REPEATABLE READ: a conflicting row committed after the snapshot raises 40001 instead.
    /// </para>
    /// <para>
    /// The partitioner sits on the endpoint's message pipe, OUTSIDE the per-consumer outbox filter, so the second
    /// message for an order only opens its transaction after the first has committed; it then sees the snapshot
    /// and is a no-op. It is per process: replicas competing on the queue can still race, and the retry policy
    /// covers that rarer case with the same end state.
    /// </para>
    /// </summary>
    public static void PartitionByOrder(IReceiveEndpointConfigurator endpoint) =>
        endpoint.UsePartitioner<OrderConfirmed>(endpoint.CreatePartitioner(Partitions), m => m.Message.OrderId);

    public async Task Consume(ConsumeContext<OrderConfirmed> context)
    {
        var m = context.Message;
        if (await db.OrderSnapshots.AnyAsync(o => o.OrderId == m.OrderId, context.CancellationToken))
        {
            return;
        }

        db.OrderSnapshots.Add(new OrderSnapshot
        {
            OrderId = m.OrderId,
            Email = m.Email,
            GrossMinor = m.AmountMinor,
            Currency = m.Currency,
            Lines = m.Lines.Select(l => new OrderSnapshotLine
            {
                Id = Guid.CreateVersion7(),
                OrderId = m.OrderId,
                ProductId = l.ProductId,
                Title = l.Title,
                UnitPriceMinor = l.UnitPriceMinor,
                // The line's allocated share of the order discount (rma_disc). An OrderConfirmed
                // published before the contract carried it deserializes to 0 — the refund then falls
                // back to the list price, capped by GrossMinor, exactly as it behaved before.
                DiscountMinor = l.DiscountMinor,
                Quantity = l.Quantity,
            }).ToList(),
        });
        await db.SaveChangesAsync(context.CancellationToken);
    }
}
