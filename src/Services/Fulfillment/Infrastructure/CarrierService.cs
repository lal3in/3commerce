using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Fulfillment;
using ThreeCommerce.Fulfillment.Domain;

namespace ThreeCommerce.Fulfillment.Infrastructure;

/// <summary>
/// Carrier integration configuration + lifecycle (mt4_3 / ADR-0042). Carriers are per-storefront (no
/// tenant-level default). Every mutation republishes the storefront's carrier readiness so Catalog's
/// go-live gate has a current copy.
/// </summary>
public sealed class CarrierService(FulfillmentDbContext db, TimeProvider clock, IPublishEndpoint publisher)
{
    public async Task<CarrierIntegration> ConfigureAsync(
        Guid tenantId, Guid storefrontId, CarrierCode carrier, string? credentialRef, CancellationToken ct)
    {
        var integration = CarrierIntegration.Configure(tenantId, storefrontId, carrier, credentialRef, clock.GetUtcNow());
        db.CarrierIntegrations.Add(integration);
        await db.SaveChangesAsync(ct);
        await PublishReadinessAsync(tenantId, storefrontId, ct);
        return integration;
    }

    // Readiness is an idempotent boolean the go-live gate reads; published after save (post-change truth).
    private async Task PublishReadinessAsync(Guid tenantId, Guid storefrontId, CancellationToken ct) =>
        await publisher.Publish(new StorefrontCarrierReadinessChanged(
            tenantId, storefrontId, await HasActiveCarrierAsync(tenantId, storefrontId, ct)), ct);

    public Task<List<CarrierIntegration>> ListAsync(Guid tenantId, Guid? storefrontId, CancellationToken ct)
    {
        var query = db.CarrierIntegrations.AsNoTracking().Where(c => c.TenantId == tenantId);
        if (storefrontId is { } sid)
        {
            query = query.Where(c => c.StorefrontId == sid);
        }

        return query.OrderBy(c => c.Carrier).ToListAsync(ct);
    }

    /// <summary>
    /// Copy a source storefront's carrier accounts onto a newly duplicated storefront (config + credential
    /// reference + status + default). Tenant-level carriers are NOT copied — they already apply to every
    /// storefront via default resolution. Idempotent: if the new storefront already has any carrier rows
    /// (e.g. on message redelivery), this is a no-op. Returns the number of accounts cloned.
    /// </summary>
    public async Task<int> CloneStorefrontCarriersAsync(
        Guid tenantId, Guid sourceStorefrontId, Guid newStorefrontId, CancellationToken ct)
    {
        var alreadyCloned = await db.CarrierIntegrations
            .AnyAsync(c => c.TenantId == tenantId && c.StorefrontId == newStorefrontId, ct);
        if (alreadyCloned)
        {
            return 0;
        }

        var sources = await db.CarrierIntegrations.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.StorefrontId == sourceStorefrontId)
            .ToListAsync(ct);
        if (sources.Count == 0)
        {
            return 0;
        }

        var now = clock.GetUtcNow();
        foreach (var source in sources)
        {
            db.CarrierIntegrations.Add(source.CloneForStorefront(newStorefrontId, now));
        }

        await db.SaveChangesAsync(ct);
        await PublishReadinessAsync(tenantId, newStorefrontId, ct);
        return sources.Count;
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
        await PublishReadinessAsync(tenantId, integration.StorefrontId, ct);
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
    /// The storefront's active default carrier — carriers are per-storefront (there is no tenant-level
    /// fallback). Null if the storefront has no active default configured.
    /// </summary>
    public Task<CarrierIntegration?> ResolveDefaultAsync(Guid tenantId, Guid storefrontId, CancellationToken ct) =>
        db.CarrierIntegrations.AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.StorefrontId == storefrontId
                && c.IsDefault && c.Status == CarrierIntegrationStatus.Active, ct);

    /// <summary>Whether the storefront has at least one active carrier (drives storefront go-live readiness).</summary>
    public Task<bool> HasActiveCarrierAsync(Guid tenantId, Guid storefrontId, CancellationToken ct) =>
        db.CarrierIntegrations.AsNoTracking()
            .AnyAsync(c => c.TenantId == tenantId && c.StorefrontId == storefrontId
                && c.Status == CarrierIntegrationStatus.Active, ct);
}
