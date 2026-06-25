using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Fulfillment.Domain;

namespace ThreeCommerce.Fulfillment.Infrastructure;

/// <summary>
/// Order holds before fulfilment (mt4_9). Evaluates automatic holds (e.g. inventory shortage),
/// captures held orders so they can fulfil on release, and lets operators place/release holds.
/// </summary>
public sealed class OrderHoldService(
    FulfillmentDbContext db, TimeProvider clock, InventoryService inventory,
    FulfilmentProcessor processor, IPublishEndpoint publisher)
{
    public Task<bool> HasActiveHoldAsync(Guid tenantId, Guid orderId, CancellationToken ct) =>
        db.OrderHolds.AnyAsync(h => h.TenantId == tenantId && h.OrderId == orderId && h.Status == HoldStatus.Active, ct);

    public Task<bool> HeldOrderExistsAsync(Guid orderId, CancellationToken ct) =>
        db.HeldOrders.AnyAsync(h => h.OrderId == orderId, ct);

    public async Task<OrderHold> PlaceHoldAsync(
        Guid tenantId, Guid orderId, HoldReason reason, string? note, string? placedBy, CancellationToken ct)
    {
        var hold = OrderHold.Place(tenantId, orderId, reason, note, placedBy, clock.GetUtcNow());
        db.OrderHolds.Add(hold);
        await db.SaveChangesAsync(ct);
        return hold;
    }

    public Task<List<OrderHold>> ListAsync(Guid tenantId, Guid orderId, CancellationToken ct) =>
        db.OrderHolds.AsNoTracking()
            .Where(h => h.TenantId == tenantId && h.OrderId == orderId)
            .OrderByDescending(h => h.CreatedAt)
            .ToListAsync(ct);

    /// <summary>Auto-place an Inventory hold if any warehouse line's demand exceeds available stock (mt4_9).</summary>
    public async Task EvaluateInventoryHoldAsync(OrderConfirmed m, CancellationToken ct)
    {
        var alreadyHeld = await db.OrderHolds.AnyAsync(
            h => h.OrderId == m.OrderId && h.Reason == HoldReason.Inventory && h.Status == HoldStatus.Active, ct);
        if (alreadyHeld)
        {
            return;
        }

        foreach (var line in m.Lines.Where(l => l.FulfilmentType == FulfilmentType.Warehouse))
        {
            var available = await inventory.AvailableAsync(m.TenantId, line.ProductId, line.VariantId, ct);
            if (available < line.Quantity)
            {
                await PlaceHoldAsync(m.TenantId, m.OrderId, HoldReason.Inventory,
                    $"Insufficient stock for product {line.ProductId} (need {line.Quantity}, have {available}).", "system", ct);
                return;
            }
        }
    }

    /// <summary>Store a held order's payload so it can fulfil once all holds clear (mt4_9).</summary>
    public async Task CaptureHeldOrderAsync(OrderConfirmed m, CancellationToken ct)
    {
        db.HeldOrders.Add(new HeldOrder
        {
            Id = Guid.CreateVersion7(),
            TenantId = m.TenantId,
            OrderId = m.OrderId,
            PayloadJson = JsonSerializer.Serialize(m),
            CreatedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Release a hold; if it was the last active one, fulfil the captured order.</summary>
    public async Task<OrderHold?> ReleaseHoldAsync(Guid tenantId, Guid holdId, CancellationToken ct)
    {
        var hold = await db.OrderHolds.SingleOrDefaultAsync(h => h.Id == holdId && h.TenantId == tenantId, ct);
        if (hold is null)
        {
            return null;
        }

        if (hold.IsActive)
        {
            hold.Release(clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
        }

        if (!await HasActiveHoldAsync(tenantId, hold.OrderId, ct))
        {
            await FulfilCapturedAsync(tenantId, hold.OrderId, ct);
        }

        return hold;
    }

    private async Task FulfilCapturedAsync(Guid tenantId, Guid orderId, CancellationToken ct)
    {
        var held = await db.HeldOrders.SingleOrDefaultAsync(h => h.OrderId == orderId && !h.Fulfilled, ct);
        if (held is null)
        {
            return;
        }

        var order = JsonSerializer.Deserialize<OrderConfirmed>(held.PayloadJson);
        if (order is not null)
        {
            await processor.FulfilAsync(order, publisher, ct);
        }

        held.MarkFulfilled();
        await db.SaveChangesAsync(ct);
    }
}
