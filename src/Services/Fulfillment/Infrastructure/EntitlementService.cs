using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Fulfillment.Domain;

namespace ThreeCommerce.Fulfillment.Infrastructure;

/// <summary>Reads customer entitlements (mt7_2). Issuance happens in FulfilmentProcessor on confirm.</summary>
public sealed class EntitlementService(FulfillmentDbContext db)
{
    public Task<List<Entitlement>> ListAsync(Guid tenantId, Guid? orderId, string? email, CancellationToken ct)
    {
        var query = db.Entitlements.AsNoTracking().Where(e => e.TenantId == tenantId);
        if (orderId is { } oid)
        {
            query = query.Where(e => e.OrderId == oid);
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            var normalized = email.Trim().ToLowerInvariant();
            query = query.Where(e => e.CustomerEmail == normalized);
        }

        return query.OrderByDescending(e => e.CreatedAt).ToListAsync(ct);
    }
}
