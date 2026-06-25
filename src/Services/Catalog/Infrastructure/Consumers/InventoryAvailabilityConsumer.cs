using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Fulfillment;

namespace ThreeCommerce.Catalog.Infrastructure.Consumers;

/// <summary>
/// Mirrors Fulfillment-owned availability onto Catalog's variant stock read model (ADR-0028).
/// Catalog never owns stock — it only reflects what Fulfillment reports. The message carries the
/// absolute available quantity, so applying it is idempotent.
/// </summary>
public sealed class InventoryAvailabilityConsumer(CatalogDbContext db) : IConsumer<InventoryAvailabilityChanged>
{
    public Task Consume(ConsumeContext<InventoryAvailabilityChanged> context)
    {
        var m = context.Message;
        return ApplyAsync(db, m.VariantId, m.Available, context.CancellationToken);
    }

    /// <summary>
    /// Project availability onto the variant's stock. Product-level availability (no variant) is
    /// not mapped to a specific variant and is ignored by this v1 projection.
    /// </summary>
    public static async Task ApplyAsync(CatalogDbContext db, Guid? variantId, int available, CancellationToken ct)
    {
        if (variantId is not { } id)
        {
            return;
        }

        var variant = await db.Variants.SingleOrDefaultAsync(v => v.Id == id, ct);
        if (variant is null)
        {
            return;
        }

        variant.StockQuantity = Math.Max(0, available);
        await db.SaveChangesAsync(ct);
    }
}
