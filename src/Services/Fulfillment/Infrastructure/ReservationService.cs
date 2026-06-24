using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Fulfillment.Domain;

namespace ThreeCommerce.Fulfillment.Infrastructure;

/// <summary>A warehouse line to reserve/confirm: (product, variant, quantity).</summary>
public sealed record ReservationLine(Guid ProductId, Guid? VariantId, int Quantity);

/// <summary>
/// Hybrid inventory reservations (mt4_2): hold / release / confirm warehouse stock against an
/// order, allocating across active locations and recording every change in the movement ledger.
/// Idempotent by (reference, movement type) so a redelivered saga message is safe.
/// </summary>
public sealed class ReservationService(FulfillmentDbContext db, TimeProvider clock, AvailabilityNotifier availability)
{
    /// <summary>Place a hold for an order. Reserves up to what is available; a shortfall is left unreserved (v1).</summary>
    public async Task ReserveAsync(Guid tenantId, Guid orderId, IReadOnlyList<ReservationLine> lines, CancellationToken ct)
    {
        if (await HasMovementAsync(orderId, InventoryMovementType.OrderReserved, ct))
        {
            return;
        }

        var now = clock.GetUtcNow();
        foreach (var line in lines)
        {
            var remaining = line.Quantity;
            foreach (var item in await AllocatableItemsAsync(tenantId, line, ct))
            {
                if (remaining <= 0)
                {
                    break;
                }

                var take = Math.Min(item.Available, remaining);
                if (take <= 0)
                {
                    continue;
                }

                item.Reserve(take, now);
                db.InventoryMovements.Add(InventoryMovement.For(
                    item, InventoryMovementType.OrderReserved, take, InventoryReferenceType.Order, orderId, now));
                remaining -= take;
            }
        }

        await db.SaveChangesAsync(ct);
        await availability.PublishAsync(tenantId, lines.Select(l => (l.ProductId, l.VariantId)), ct);
    }

    /// <summary>Release a hold (order cancelled before confirmation).</summary>
    public async Task ReleaseAsync(Guid orderId, CancellationToken ct)
    {
        if (await HasMovementAsync(orderId, InventoryMovementType.OrderCancelled, ct))
        {
            return;
        }

        var now = clock.GetUtcNow();
        var released = await ReservedByItemAsync(orderId, ct);
        foreach (var (item, quantity) in released)
        {
            item.Release(quantity, now);
            db.InventoryMovements.Add(InventoryMovement.For(
                item, InventoryMovementType.OrderCancelled, quantity, InventoryReferenceType.Order, orderId, now));
        }

        await db.SaveChangesAsync(ct);
        await PublishAvailabilityAsync(released.Select(r => r.Item), ct);
    }

    /// <summary>
    /// Confirm a sale. If the order was pre-reserved, consume those holds; otherwise (the
    /// checkout-time hold lands in mt7_1) reserve-and-consume the given warehouse lines in one step.
    /// </summary>
    public async Task ConfirmAsync(Guid tenantId, Guid orderId, IReadOnlyList<ReservationLine> lines, CancellationToken ct)
    {
        if (await HasMovementAsync(orderId, InventoryMovementType.OrderConfirmed, ct))
        {
            return;
        }

        var now = clock.GetUtcNow();
        var reserved = await ReservedByItemAsync(orderId, ct);
        if (reserved.Count > 0)
        {
            foreach (var (item, quantity) in reserved)
            {
                item.ConfirmReservation(quantity, now);
                db.InventoryMovements.Add(InventoryMovement.For(
                    item, InventoryMovementType.OrderConfirmed, quantity, InventoryReferenceType.Order, orderId, now));
            }
        }
        else
        {
            foreach (var line in lines)
            {
                var remaining = line.Quantity;
                foreach (var item in await AllocatableItemsAsync(tenantId, line, ct))
                {
                    if (remaining <= 0)
                    {
                        break;
                    }

                    var take = Math.Min(item.QuantityOnHand, remaining);
                    if (take <= 0)
                    {
                        continue;
                    }

                    item.ConfirmReservation(take, now); // no prior hold → just decrements on-hand
                    db.InventoryMovements.Add(InventoryMovement.For(
                        item, InventoryMovementType.OrderConfirmed, take, InventoryReferenceType.Order, orderId, now));
                    remaining -= take;
                }
            }
        }

        await db.SaveChangesAsync(ct);
        var confirmedKeys = reserved.Count > 0
            ? reserved.Select(r => (r.Item.ProductId, r.Item.VariantId))
            : lines.Select(l => (l.ProductId, l.VariantId));
        await availability.PublishAsync(tenantId, confirmedKeys, ct);
    }

    private async Task PublishAvailabilityAsync(IEnumerable<InventoryItem> items, CancellationToken ct)
    {
        foreach (var byTenant in items.GroupBy(i => i.TenantId))
        {
            await availability.PublishAsync(byTenant.Key, byTenant.Select(i => (i.ProductId, i.VariantId)), ct);
        }
    }

    private Task<bool> HasMovementAsync(Guid orderId, InventoryMovementType type, CancellationToken ct) =>
        db.InventoryMovements.AnyAsync(m => m.ReferenceId == orderId && m.Type == type, ct);

    private async Task<List<(InventoryItem Item, int Quantity)>> ReservedByItemAsync(Guid orderId, CancellationToken ct)
    {
        var moves = await db.InventoryMovements
            .Where(m => m.ReferenceId == orderId && m.Type == InventoryMovementType.OrderReserved)
            .ToListAsync(ct);

        var result = new List<(InventoryItem, int)>();
        foreach (var grouped in moves.GroupBy(m => m.InventoryItemId))
        {
            var item = await db.InventoryItems.SingleOrDefaultAsync(i => i.Id == grouped.Key, ct);
            if (item is not null)
            {
                result.Add((item, grouped.Sum(m => m.Quantity)));
            }
        }

        return result;
    }

    private Task<List<InventoryItem>> AllocatableItemsAsync(Guid tenantId, ReservationLine line, CancellationToken ct) =>
        db.InventoryItems
            .Where(i => i.TenantId == tenantId && i.ProductId == line.ProductId && i.VariantId == line.VariantId)
            .Join(db.InventoryLocations.Where(l => l.Status == LocationStatus.Active),
                i => i.LocationId, l => l.Id, (i, _) => i)
            .OrderByDescending(i => i.QuantityOnHand - i.QuantityReserved)
            .ToListAsync(ct);
}
