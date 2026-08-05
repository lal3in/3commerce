using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Infrastructure.Projections;

/// <summary>Keeps the local ProductTypeShippingPolicyCopy current from Catalog's policy events (ADR-0008).</summary>
public sealed class ProductTypeShippingPolicyChangedConsumer(OrderingDbContext db)
    : IConsumer<ProductTypeShippingPolicyChanged>
{
    public async Task Consume(ConsumeContext<ProductTypeShippingPolicyChanged> context)
    {
        var m = context.Message;
        var copy = await db.ProductTypeShippingPolicyCopies
            .SingleOrDefaultAsync(p => p.TenantId == m.TenantId, context.CancellationToken);
        if (copy is null)
        {
            copy = new ProductTypeShippingPolicyCopy { TenantId = m.TenantId };
            db.ProductTypeShippingPolicyCopies.Add(copy);
        }

        copy.RequiresShippingTypes = m.RequiresShippingTypes;
        await db.SaveChangesAsync(context.CancellationToken);
    }
}
