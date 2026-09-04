using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Infrastructure.Projections;

/// <summary>Keeps the local PromotionCopy current from Catalog's PromotionChanged events (ADR-0051 /
/// ADR-0008). Idempotent upsert: every field is assigned on BOTH the insert and the update path, so a
/// redelivered or re-published message converges on the same row rather than duplicating it.</summary>
public sealed class PromotionChangedConsumer(OrderingDbContext db) : IConsumer<PromotionChanged>
{
    public async Task Consume(ConsumeContext<PromotionChanged> context)
    {
        var m = context.Message;
        var copy = await db.PromotionCopies.SingleOrDefaultAsync(
            p => p.PromotionId == m.PromotionId, context.CancellationToken);
        if (copy is null)
        {
            copy = new PromotionCopy { PromotionId = m.PromotionId };
            db.PromotionCopies.Add(copy);
        }

        copy.TenantId = m.TenantId;
        copy.StorefrontId = m.StorefrontId;
        copy.Name = m.Name;
        copy.Currency = m.Currency;
        copy.Scope = m.Scope;
        copy.ProductId = m.ProductId;
        copy.MinimumAmountMinor = m.MinimumAmountMinor;
        copy.MinimumQuantity = m.MinimumQuantity;
        copy.GrantsFreeShipping = m.GrantsFreeShipping;
        copy.PercentOff = m.PercentOff;
        copy.DiscountAmountMinor = m.DiscountAmountMinor;
        copy.Combinable = m.Combinable;
        copy.Active = m.Active;
        copy.ActiveFrom = m.ActiveFrom;
        copy.ActiveUntil = m.ActiveUntil;
        await db.SaveChangesAsync(context.CancellationToken);
    }
}
