using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Fulfillment;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Catalog.Domain;

namespace ThreeCommerce.Catalog.Infrastructure.Consumers;

/// <summary>
/// Mirror the cross-service go-live signals onto Catalog's StorefrontServiceReadiness read model
/// (ADR-0042). Each event carries the current truth, so applying it is idempotent.
/// </summary>
public sealed class StorefrontCarrierReadinessConsumer(CatalogDbContext db)
    : IConsumer<StorefrontCarrierReadinessChanged>
{
    public async Task Consume(ConsumeContext<StorefrontCarrierReadinessChanged> context)
    {
        var m = context.Message;
        var row = await Upsert(db, m.StorefrontId, m.TenantId, context.CancellationToken);
        row.HasActiveCarrier = m.HasActiveCarrier;
        await db.SaveChangesAsync(context.CancellationToken);
    }

    internal static async Task<StorefrontServiceReadiness> Upsert(
        CatalogDbContext db, Guid storefrontId, Guid tenantId, CancellationToken ct)
    {
        var row = await db.StorefrontServiceReadiness.SingleOrDefaultAsync(r => r.StorefrontId == storefrontId, ct);
        if (row is null)
        {
            row = new StorefrontServiceReadiness { StorefrontId = storefrontId, TenantId = tenantId };
            db.StorefrontServiceReadiness.Add(row);
        }
        else
        {
            row.TenantId = tenantId;
        }

        return row;
    }
}

public sealed class StorefrontPaymentReadinessConsumer(CatalogDbContext db)
    : IConsumer<StorefrontPaymentReadinessChanged>
{
    public async Task Consume(ConsumeContext<StorefrontPaymentReadinessChanged> context)
    {
        var m = context.Message;
        var row = await StorefrontCarrierReadinessConsumer.Upsert(db, m.StorefrontId, m.TenantId, context.CancellationToken);
        row.HasActivePaymentAccount = m.HasActivePaymentAccount;
        await db.SaveChangesAsync(context.CancellationToken);
    }
}
