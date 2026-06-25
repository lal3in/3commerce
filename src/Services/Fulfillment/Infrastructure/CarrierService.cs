using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Fulfillment.Domain;

namespace ThreeCommerce.Fulfillment.Infrastructure;

/// <summary>
/// Carrier integration configuration + lifecycle (mt4_3). A tenant configures carriers at the
/// tenant level (the default) and may override per storefront. Resolution prefers a storefront
/// override over the tenant default.
/// </summary>
public sealed class CarrierService(FulfillmentDbContext db, TimeProvider clock)
{
    public async Task<CarrierIntegration> ConfigureAsync(
        Guid tenantId, Guid? storefrontId, CarrierCode carrier, string? credentialRef, CancellationToken ct)
    {
        var integration = CarrierIntegration.Configure(tenantId, storefrontId, carrier, credentialRef, clock.GetUtcNow());
        db.CarrierIntegrations.Add(integration);
        await db.SaveChangesAsync(ct);
        return integration;
    }

    public Task<List<CarrierIntegration>> ListAsync(Guid tenantId, Guid? storefrontId, CancellationToken ct)
    {
        var query = db.CarrierIntegrations.AsNoTracking().Where(c => c.TenantId == tenantId);
        if (storefrontId is { } sid)
        {
            query = query.Where(c => c.StorefrontId == sid);
        }

        return query.OrderBy(c => c.Carrier).ToListAsync(ct);
    }

    /// <summary>Apply a lifecycle transition. Returns null if the integration is not found for the tenant.</summary>
    public async Task<CarrierIntegration?> TransitionAsync(
        Guid tenantId, Guid id, Action<CarrierIntegration, DateTimeOffset> transition, CancellationToken ct)
    {
        var integration = await db.CarrierIntegrations.SingleOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId, ct);
        if (integration is null)
        {
            return null;
        }

        transition(integration, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return integration;
    }

    /// <summary>Make an active carrier the single default within its (tenant, storefront) scope.</summary>
    public async Task<CarrierIntegration?> MakeDefaultAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        var integration = await db.CarrierIntegrations.SingleOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId, ct);
        if (integration is null)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        var peers = await db.CarrierIntegrations
            .Where(c => c.TenantId == tenantId && c.StorefrontId == integration.StorefrontId && c.Id != id && c.IsDefault)
            .ToListAsync(ct);
        foreach (var peer in peers)
        {
            peer.ClearDefault(now);
        }

        integration.MarkDefault(now);
        await db.SaveChangesAsync(ct);
        return integration;
    }

    /// <summary>
    /// The active default carrier for a storefront: a storefront-scoped default wins; otherwise the
    /// tenant-level default. Null if none is configured.
    /// </summary>
    public async Task<CarrierIntegration?> ResolveDefaultAsync(Guid tenantId, Guid? storefrontId, CancellationToken ct)
    {
        if (storefrontId is { } sid)
        {
            var storefrontDefault = await db.CarrierIntegrations.AsNoTracking()
                .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.StorefrontId == sid
                    && c.IsDefault && c.Status == CarrierIntegrationStatus.Active, ct);
            if (storefrontDefault is not null)
            {
                return storefrontDefault;
            }
        }

        return await db.CarrierIntegrations.AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.StorefrontId == null
                && c.IsDefault && c.Status == CarrierIntegrationStatus.Active, ct);
    }
}
