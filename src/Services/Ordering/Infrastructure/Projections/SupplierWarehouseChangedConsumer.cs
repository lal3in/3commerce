using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Entity;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Infrastructure.Projections;

/// <summary>
/// Keeps the local <see cref="SupplierWarehouseCopy"/> current from the Entity service's
/// <see cref="SupplierWarehouseChanged"/> (ADR-0008). Each event carries the current warehouse address, so
/// the upsert is idempotent — mirrors <c>SupplierApprovalChangedConsumer</c>. "Collect at warehouse"
/// checkout reads this to stamp the warehouse address onto the order.
/// </summary>
public sealed class SupplierWarehouseChangedConsumer(OrderingDbContext db) : IConsumer<SupplierWarehouseChanged>
{
    public async Task Consume(ConsumeContext<SupplierWarehouseChanged> context)
    {
        var m = context.Message;
        var copy = await db.SupplierWarehouseCopies.SingleOrDefaultAsync(
            s => s.SupplierId == m.SupplierId, context.CancellationToken);
        if (copy is null)
        {
            copy = new SupplierWarehouseCopy
            {
                SupplierId = m.SupplierId,
                Name = m.SupplierName,
                Line1 = m.Line1,
                City = m.City,
                Postcode = m.Postcode,
                CountryCode = m.CountryCode,
            };
            db.SupplierWarehouseCopies.Add(copy);
        }

        copy.TenantId = m.TenantId;
        copy.Name = m.SupplierName;
        copy.Line1 = m.Line1;
        copy.Line2 = m.Line2;
        copy.City = m.City;
        copy.Region = m.Region;
        copy.Postcode = m.Postcode;
        copy.CountryCode = m.CountryCode;
        copy.UpdatedAt = context.SentTime ?? DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(context.CancellationToken);
    }
}
