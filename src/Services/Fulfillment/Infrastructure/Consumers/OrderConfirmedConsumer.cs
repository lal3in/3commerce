using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Fulfillment;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Fulfillment.Domain.Carriers;
using ThreeCommerce.Fulfillment.Domain.Dropship;
using ThreeCommerce.Fulfillment.Infrastructure.Dropship;

namespace ThreeCommerce.Fulfillment.Infrastructure.Consumers;

/// <summary>
/// Turns a confirmed order into shipments, one per fulfillment source (ADR-0003), consumes warehouse
/// stock (mt4_2), and forwards dropship lines to suppliers (mt4_4b). Idempotent by order.
/// </summary>
public sealed class OrderConfirmedConsumer(
    FulfillmentDbContext db, TimeProvider time, ReservationService reservations, SupplierOrderService supplierOrders)
    : IConsumer<OrderConfirmed>
{
    public async Task Consume(ConsumeContext<OrderConfirmed> context)
    {
        var m = context.Message;
        if (await db.Shipments.AnyAsync(s => s.OrderId == m.OrderId, context.CancellationToken))
        {
            return;
        }

        // Consume on-hand for warehouse lines first (idempotent by order); ConfirmAsync honours any
        // prior reservation, otherwise decrements directly. Done before shipments so a partial retry
        // re-confirms (idempotent) rather than skipping stock.
        var warehouseLines = m.Lines
            .Where(l => l.FulfilmentType == FulfilmentType.Warehouse)
            .Select(l => new ReservationLine(l.ProductId, l.VariantId, l.Quantity))
            .ToList();
        if (warehouseLines.Count > 0)
        {
            await reservations.ConfirmAsync(m.TenantId, m.OrderId, warehouseLines, context.CancellationToken);
        }

        // Forward dropship lines to their supplier (mt4_4b), one supplier order per supplier.
        var destination = new ShipAddress(m.ShipTo.Name, m.ShipTo.Line1, m.ShipTo.City, m.ShipTo.Postcode, m.ShipTo.Country);
        foreach (var bySupplier in m.Lines
            .Where(l => l.FulfilmentType == FulfilmentType.Dropship && l.SupplierId is not null)
            .GroupBy(l => l.SupplierId!.Value))
        {
            var items = bySupplier
                .Select(l => new SupplierOrderItem(l.ProductId, l.VariantId, null, l.Quantity))
                .ToList();
            await supplierOrders.ForwardAsync(
                m.TenantId,
                new SupplierOrderRequest(m.OrderId, bySupplier.Key, m.Email, destination, items),
                context.CancellationToken);
        }

        foreach (var group in m.Lines.GroupBy(l => l.FulfilmentType))
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
            await context.Publish(new ShipmentCreated(shipment.Id, m.OrderId, group.Key));
        }

        await db.SaveChangesAsync(context.CancellationToken);
    }
}
