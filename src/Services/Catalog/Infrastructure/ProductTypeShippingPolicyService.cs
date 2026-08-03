using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Catalog.Domain;

namespace ThreeCommerce.Catalog.Infrastructure;

/// <summary>
/// Reads/writes a tenant's <see cref="ProductTypeShippingPolicy"/> (which product types require a
/// carrier). When a tenant has never set one, a transient default (Physical ships) is returned so
/// readiness resolution always has an answer without a persisted row.
/// </summary>
public sealed class ProductTypeShippingPolicyService(CatalogDbContext db, TimeProvider clock)
{
    /// <summary>The tenant's policy, or a transient default (not persisted) if none is set.</summary>
    public async Task<ProductTypeShippingPolicy> GetOrDefaultAsync(Guid tenantId, CancellationToken ct)
    {
        var existing = await db.ProductTypeShippingPolicies.AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId, ct);
        return existing ?? ProductTypeShippingPolicy.Create(tenantId, clock.GetUtcNow());
    }

    /// <summary>Upsert the tenant's policy to exactly the given set of shippable product types.</summary>
    public async Task<ProductTypeShippingPolicy> SetAsync(
        Guid tenantId, IEnumerable<ProductType> types, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var policy = await db.ProductTypeShippingPolicies.FirstOrDefaultAsync(p => p.TenantId == tenantId, ct);
        if (policy is null)
        {
            policy = ProductTypeShippingPolicy.Create(tenantId, now);
            db.ProductTypeShippingPolicies.Add(policy);
        }

        policy.SetTypes(types, now);
        await db.SaveChangesAsync(ct);
        return policy;
    }
}
