using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Audit.Infrastructure;
using ThreeCommerce.BuildingBlocks.Infrastructure.Tenancy;
using ThreeCommerce.Catalog.Domain;
using ThreeCommerce.Catalog.Infrastructure;
using ThreeCommerce.Fulfillment.Infrastructure;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill.Storefronts;

/// <summary>
/// The central audit timeline (Audit service, no RLS — it is the platform's projection). Catalog's audit is
/// publish-only, so this is where <c>catalog.storefront.duplicate</c> entries live: the duplicate's id and the name it
/// was given (= the <c>Name</c> the live <c>StorefrontDuplicated</c> carried). They do NOT record the source — Catalog's
/// <c>Storefront.DuplicatedFromStorefrontId</c> does, for copies made since it existed.
/// </summary>
public sealed class DuplicationAuditSource(AuditDbContext db)
{
    public const string DuplicateAction = "catalog.storefront.duplicate";

    public async Task<List<DuplicationFact>> DuplicationsAsync(CancellationToken ct)
    {
        var entries = await db.AuditEntries.AsNoTracking()
            .Where(e => e.Action == DuplicateAction && e.ResourceType == "Storefront" && e.Outcome == "Success")
            .Select(e => new { e.TenantId, e.ResourceId, e.Summary })
            .ToListAsync(ct);
        return entries
            .Select(e => Guid.TryParse(e.ResourceId, out var id) ? new DuplicationFact(e.TenantId, id, e.Summary) : null)
            .OfType<DuplicationFact>()
            .ToList();
    }
}

/// <summary>Catalog's storefronts and publications, read inside the RLS tenant scope (ADR-0024).</summary>
public sealed class StorefrontCatalogSource(CatalogDbContext db, ITenantScope scope)
{
    /// <summary>Every tenant with a storefront. Enumerating across tenants is a platform-scope read.</summary>
    public Task<List<Guid>> TenantsAsync(CancellationToken ct) =>
        scope.RunAsync(db, TenantContext.Platform(), () => db.Storefronts.AsNoTracking()
            .Select(s => s.TenantId).Distinct().OrderBy(t => t).ToListAsync(ct), ct);

    public Task<List<StorefrontFact>> StorefrontsAsync(Guid tenantId, CancellationToken ct) =>
        scope.RunAsync(db, TenantContext.ForTenant(tenantId), () => db.Storefronts.AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .Select(s => new StorefrontFact(s.Id, s.TenantId, s.Name, s.CreatedAt, s.State == StorefrontState.Archived, s.DuplicatedFromStorefrontId))
            .ToListAsync(ct), ct);

    /// <summary>
    /// The tenant's publications that have ever been published, of products that still exist — a duplication skips a
    /// source publication whose product is gone, so such a publication never takes part in the lineage comparison.
    /// </summary>
    public Task<List<PublicationFact>> PublicationsAsync(Guid tenantId, CancellationToken ct) =>
        scope.RunAsync(db, TenantContext.ForTenant(tenantId), () => db.ProductPublications.AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.PublishedAt != null && db.Products.Any(pr => pr.Id == p.ProductId))
            .Select(p => new PublicationFact(p.StorefrontId, p.ProductId, p.CreatedAt, p.PublishedAt!.Value))
            .ToListAsync(ct), ct);
}

/// <summary>Payments' per-storefront payment accounts — what <c>StorefrontDuplicatedConsumer</c> copies.</summary>
public sealed class PaymentAccountSource(PaymentsDbContext db, ITenantScope scope)
{
    public async Task<List<ConfigRowFact>> RowsAsync(Guid tenantId, CancellationToken ct)
    {
        var accounts = await scope.RunAsync(db, TenantContext.ForTenant(tenantId), () => db.PaymentAccounts.AsNoTracking()
            .Where(a => a.TenantId == tenantId).ToListAsync(ct), ct);
        return accounts.Select(ConfigRowFact.From).ToList();
    }
}

/// <summary>Fulfillment's per-storefront carrier integrations — what <c>FulfillmentStorefrontDuplicatedConsumer</c> copies.</summary>
public sealed class CarrierIntegrationSource(FulfillmentDbContext db, ITenantScope scope)
{
    public async Task<List<ConfigRowFact>> RowsAsync(Guid tenantId, CancellationToken ct)
    {
        var carriers = await scope.RunAsync(db, TenantContext.ForTenant(tenantId), () => db.CarrierIntegrations.AsNoTracking()
            .Where(c => c.TenantId == tenantId).ToListAsync(ct), ct);
        return carriers.Select(ConfigRowFact.From).ToList();
    }
}
