using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Entity;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Infrastructure.Projections;

/// <summary>
/// Keeps the local <see cref="SupplierApprovalCopy"/> current from the Entity service's
/// <see cref="SupplierApprovalChanged"/> (DECISION A / ADR-0008). Each event carries the current truth, so
/// the upsert is idempotent — mirrors <c>OfferChangedConsumer</c>. Checkout gates offer resolution on this.
/// </summary>
public sealed class SupplierApprovalChangedConsumer(OrderingDbContext db) : IConsumer<SupplierApprovalChanged>
{
    public async Task Consume(ConsumeContext<SupplierApprovalChanged> context)
    {
        var m = context.Message;
        var copy = await db.SupplierApprovalCopies.SingleOrDefaultAsync(
            s => s.SupplierId == m.SupplierId, context.CancellationToken);
        if (copy is null)
        {
            copy = new SupplierApprovalCopy { SupplierId = m.SupplierId };
            db.SupplierApprovalCopies.Add(copy);
        }

        copy.TenantId = m.TenantId;
        copy.Approved = m.Approved;
        copy.UpdatedAt = context.SentTime ?? DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(context.CancellationToken);
    }
}
