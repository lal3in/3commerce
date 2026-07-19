using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Fulfillment;
using ThreeCommerce.Fulfillment.Domain;

namespace ThreeCommerce.Fulfillment.Infrastructure;

/// <summary>
/// Publishes the new sellable availability for affected (product, variant) keys so Catalog can
/// keep its read model in sync (ADR-0028 single stock owner). The message carries the absolute
/// quantity, so a lost/duplicated publish only makes the mirror briefly stale, never wrong.
/// </summary>
public sealed class AvailabilityNotifier(FulfillmentDbContext db, IPublishEndpoint publisher)
{
    public async Task PublishAsync(
        Guid tenantId, IEnumerable<(Guid ProductId, Guid? VariantId)> keys, CancellationToken ct)
    {
        foreach (var key in keys.Distinct())
        {
            var rows = await db.InventoryItems.AsNoTracking()
                .Where(i => i.TenantId == tenantId && i.ProductId == key.ProductId && i.VariantId == key.VariantId)
                .Join(db.InventoryLocations.AsNoTracking().Where(l => l.Status == LocationStatus.Active),
                    i => i.LocationId, l => l.Id, (i, _) => new { i.QuantityOnHand, i.QuantityReserved })
                .ToListAsync(ct);
            var available = rows.Sum(r => Math.Max(0, r.QuantityOnHand - r.QuantityReserved));
            await publisher.Publish(new InventoryAvailabilityChanged(tenantId, key.ProductId, key.VariantId, available), ct);
        }

        // The bus outbox (AddServiceBus<TDbContext> → UseBusOutbox) captures Publish into outbox
        // rows on THIS DbContext — they only reach the broker when the context is saved. Every
        // caller invokes this notifier AFTER its own SaveChangesAsync (it must: availability is
        // computed from committed rows), so without this save the events are silently dropped
        // and Catalog's stock mirror never updates.
        await db.SaveChangesAsync(ct);
    }
}
