using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Fulfillment.Domain;

namespace ThreeCommerce.Fulfillment.Infrastructure;

/// <summary>
/// Inventory locations and on-hand stock (mt4_1). Locations are linked to an owning Entity;
/// stock is fed per (product, variant, location) by admins or supplier feeds.
/// </summary>
public sealed class InventoryService(FulfillmentDbContext db, TimeProvider clock, AvailabilityNotifier availability)
{
    public async Task<InventoryLocation> CreateLocationAsync(
        Guid tenantId, Guid entityId, Guid? addressId, string name, LocationKind kind, CancellationToken ct)
    {
        var location = InventoryLocation.Create(tenantId, entityId, addressId, name, kind, clock.GetUtcNow());
        db.InventoryLocations.Add(location);
        await db.SaveChangesAsync(ct);
        return location;
    }

    public Task<List<InventoryLocation>> ListLocationsAsync(Guid tenantId, CancellationToken ct) =>
        db.InventoryLocations.AsNoTracking()
            .Where(l => l.TenantId == tenantId)
            .OrderBy(l => l.Name)
            .ToListAsync(ct);

    /// <summary>
    /// Stock feed: set the absolute on-hand for a (product, variant) at a location, creating the
    /// row on first sight. Validates the location belongs to the tenant.
    /// </summary>
    public async Task<InventoryItem> SetStockAsync(
        Guid tenantId, Guid locationId, Guid productId, Guid? variantId, int onHand, CancellationToken ct)
    {
        var locationExists = await db.InventoryLocations
            .AnyAsync(l => l.Id == locationId && l.TenantId == tenantId, ct);
        if (!locationExists)
        {
            throw new FulfillmentRuleException("Inventory location not found for this tenant.");
        }

        var item = await db.InventoryItems.SingleOrDefaultAsync(
            i => i.TenantId == tenantId && i.LocationId == locationId
                 && i.ProductId == productId && i.VariantId == variantId, ct);

        if (item is null)
        {
            item = InventoryItem.Create(tenantId, locationId, productId, variantId, onHand, clock.GetUtcNow());
            db.InventoryItems.Add(item);
        }
        else
        {
            item.SetOnHand(onHand, clock.GetUtcNow());
        }

        await db.SaveChangesAsync(ct);
        await availability.PublishAsync(tenantId, [(productId, variantId)], ct);
        return item;
    }

    public Task<List<InventoryItem>> ListStockAsync(Guid tenantId, Guid? productId, CancellationToken ct)
    {
        var query = db.InventoryItems.AsNoTracking().Where(i => i.TenantId == tenantId);
        if (productId is { } pid)
        {
            query = query.Where(i => i.ProductId == pid);
        }

        return query.OrderBy(i => i.ProductId).ToListAsync(ct);
    }

    /// <summary>Total sellable stock for a (product, variant) summed across active locations.</summary>
    public async Task<int> AvailableAsync(Guid tenantId, Guid productId, Guid? variantId, CancellationToken ct)
    {
        var rows = await db.InventoryItems.AsNoTracking()
            .Where(i => i.TenantId == tenantId && i.ProductId == productId && i.VariantId == variantId)
            .Join(db.InventoryLocations.AsNoTracking().Where(l => l.Status == LocationStatus.Active),
                i => i.LocationId, l => l.Id, (i, _) => i)
            .Select(i => new { i.QuantityOnHand, i.QuantityReserved })
            .ToListAsync(ct);
        return rows.Sum(r => Math.Max(0, r.QuantityOnHand - r.QuantityReserved));
    }
}
