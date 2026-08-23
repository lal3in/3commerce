using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Entity;
using ThreeCommerce.Catalog.Domain;

namespace ThreeCommerce.Catalog.Infrastructure.Consumers;

/// <summary>
/// Projects a supplier's approval state onto Catalog's local <see cref="SupplierApprovalCopy"/> read model
/// (DECISION A), fed by the Entity service's <see cref="SupplierApprovalChanged"/>. Each event carries the
/// current truth, so the upsert is idempotent — mirrors <c>CurrencyProjectionConsumer</c>. Storefront
/// availability and offer-as-price gate on this: an unapproved supplier's offer never counts.
/// </summary>
public sealed class SupplierApprovalConsumer(CatalogDbContext db) : IConsumer<SupplierApprovalChanged>
{
    public async Task Consume(ConsumeContext<SupplierApprovalChanged> context)
    {
        var m = context.Message;
        var row = await db.SupplierApprovalCopies.SingleOrDefaultAsync(
            s => s.SupplierId == m.SupplierId, context.CancellationToken);
        if (row is null)
        {
            row = new SupplierApprovalCopy { SupplierId = m.SupplierId };
            db.SupplierApprovalCopies.Add(row);
        }

        row.TenantId = m.TenantId;
        row.Approved = m.Approved;
        row.UpdatedAt = context.SentTime ?? DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(context.CancellationToken);
    }
}
