using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Fulfillment;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Fulfillment.Domain.Carriers;
using ThreeCommerce.Fulfillment.Domain.Dropship;
using ThreeCommerce.Fulfillment.Infrastructure.Dropship;

namespace ThreeCommerce.Fulfillment.Infrastructure;

/// <summary>
/// The actual fulfilment of a confirmed order (mt4_2/4b/7): consume warehouse stock, forward dropship
/// lines, create shipments. Extracted so both the OrderConfirmed consumer (when not held) and the
/// hold-release flow (from a captured payload, mt4_9) can run it. Idempotent by order.
/// </summary>
public sealed class FulfilmentProcessor(
    FulfillmentDbContext db, TimeProvider time, ReservationService reservations, SupplierOrderService supplierOrders)
{
    public async Task FulfilAsync(OrderConfirmed m, IPublishEndpoint publisher, CancellationToken ct)
    {
        // Idempotent: an order is fulfilled once. Digital/service lines are fulfilled by the dedicated
        // Entitlement service (mt7_2), which consumes OrderConfirmed independently; Fulfillment ships only
        // physical lines, so a digital-only order is a no-op here.
        if (await db.Shipments.AnyAsync(s => s.OrderId == m.OrderId, ct))
        {
            return;
        }

        var warehouseLines = m.Lines
            .Where(l => l.FulfilmentType == FulfilmentType.Warehouse)
            .Select(l => new ReservationLine(l.ProductId, l.VariantId, l.Quantity))
            .ToList();
        if (warehouseLines.Count > 0)
        {
            await reservations.ConfirmAsync(m.TenantId, m.OrderId, warehouseLines, ct);
        }

        var destination = new ShipAddress(m.ShipTo.Name, m.ShipTo.Line1, m.ShipTo.City, m.ShipTo.Postcode, m.ShipTo.Country);
        foreach (var bySupplier in m.Lines
            .Where(l => l.FulfilmentType == FulfilmentType.Dropship && l.SupplierId is not null)
            .GroupBy(l => l.SupplierId!.Value))
        {
            var items = bySupplier.Select(l => new SupplierOrderItem(l.ProductId, l.VariantId, null, l.Quantity)).ToList();
            await supplierOrders.ForwardAsync(
                m.TenantId, new SupplierOrderRequest(m.OrderId, bySupplier.Key, m.Email, destination, items), ct);
        }

        // Shipments only for physical lines; digital/service lines are entitlements (Entitlement service).
        foreach (var group in m.Lines.Where(l => !IsDigital(l.FulfilmentType)).GroupBy(l => l.FulfilmentType))
        {
            var shipment = new Shipment
            {
                Id = Guid.CreateVersion7(),
                TenantId = m.TenantId,
                OrderId = m.OrderId,
                FulfillmentSource = group.Key.ToString(),
                Status = ShipmentStatus.Created,
                Email = m.Email,
                CreatedAt = time.GetUtcNow(),
                Lines = group.Select(l => new ShipmentLine
                {
                    Id = Guid.CreateVersion7(),
                    ProductId = l.ProductId,
                    Title = l.Title,
                    Quantity = l.Quantity,
                }).ToList(),
            };
            db.Shipments.Add(shipment);
            await publisher.Publish(new ShipmentCreated(shipment.Id, m.OrderId, group.Key), ct);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Digital/service lines are fulfilled as entitlements (Entitlement service), not shipped.</summary>
    private static bool IsDigital(FulfilmentType fulfilment) => fulfilment
        is FulfilmentType.DigitalDownload or FulfilmentType.Subscription or FulfilmentType.Usage or FulfilmentType.ManualService;
}
