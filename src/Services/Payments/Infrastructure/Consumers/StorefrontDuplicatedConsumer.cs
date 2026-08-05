using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Infrastructure.Consumers;

/// <summary>
/// When a storefront is duplicated, copy the source storefront's payment accounts (descriptor, mode,
/// external reference, default flag and lifecycle state) onto the new storefront so the duplicate can
/// take payment with the same accounts (ADR-0042). Idempotent by (tenant, new storefront) — a
/// redelivered message is a no-op.
/// </summary>
public sealed class StorefrontDuplicatedConsumer(PaymentsDbContext db, TimeProvider clock)
    : IConsumer<StorefrontDuplicated>
{
    public async Task Consume(ConsumeContext<StorefrontDuplicated> context)
    {
        var m = context.Message;
        var ct = context.CancellationToken;

        var alreadyCloned = await db.PaymentAccounts
            .AnyAsync(a => a.TenantId == m.TenantId && a.StorefrontId == m.NewStorefrontId, ct);
        if (alreadyCloned)
        {
            return;
        }

        var sources = await db.PaymentAccounts.AsNoTracking()
            .Where(a => a.TenantId == m.TenantId && a.StorefrontId == m.SourceStorefrontId)
            .ToListAsync(ct);
        if (sources.Count == 0)
        {
            return;
        }

        var now = clock.GetUtcNow();
        foreach (var source in sources)
        {
            db.PaymentAccounts.Add(source.CloneForStorefront(m.NewStorefrontId, now));
        }

        await db.SaveChangesAsync(ct);
    }
}
