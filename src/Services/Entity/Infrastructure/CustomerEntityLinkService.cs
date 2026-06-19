using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Entity.Domain;

namespace ThreeCommerce.Entity.Infrastructure;

/// <summary>
/// Customer↔entity links (mt2_7): a tenant customer (Identity principal) related to one or more
/// entities with typed roles. De-linking ends the link, preserving history. A customer may not
/// hold two active links of the same role to the same entity.
/// </summary>
public sealed class CustomerEntityLinkService(EntityDbContext db, TimeProvider timeProvider)
{
    public async Task<CustomerEntityLink> LinkAsync(
        Guid tenantId, Guid entityId, Guid customerPrincipalId, CustomerEntityRole role, Guid linkedByPrincipalId, CancellationToken cancellationToken)
    {
        var alreadyLinked = await db.CustomerEntityLinks.AnyAsync(
            l => l.TenantId == tenantId && l.EntityId == entityId && l.CustomerPrincipalId == customerPrincipalId && l.Role == role && l.EffectiveTo == null,
            cancellationToken);
        if (alreadyLinked)
        {
            throw new DomainRuleException("An active link with this role already exists for this customer and entity.");
        }

        var link = CustomerEntityLink.Create(tenantId, customerPrincipalId, entityId, role, linkedByPrincipalId, timeProvider.GetUtcNow());
        db.CustomerEntityLinks.Add(link);
        await db.SaveChangesAsync(cancellationToken);
        return link;
    }

    public Task<List<CustomerEntityLink>> ListForEntityAsync(Guid tenantId, Guid entityId, bool activeOnly, CancellationToken cancellationToken) =>
        db.CustomerEntityLinks.AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.EntityId == entityId && (!activeOnly || l.EffectiveTo == null))
            .OrderByDescending(l => l.EffectiveFrom)
            .ToListAsync(cancellationToken);

    public Task<List<CustomerEntityLink>> ListForCustomerAsync(Guid tenantId, Guid customerPrincipalId, bool activeOnly, CancellationToken cancellationToken) =>
        db.CustomerEntityLinks.AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.CustomerPrincipalId == customerPrincipalId && (!activeOnly || l.EffectiveTo == null))
            .OrderByDescending(l => l.EffectiveFrom)
            .ToListAsync(cancellationToken);

    public async Task<CustomerEntityLink?> UnlinkAsync(Guid tenantId, Guid linkId, CancellationToken cancellationToken)
    {
        var link = await db.CustomerEntityLinks.SingleOrDefaultAsync(l => l.Id == linkId && l.TenantId == tenantId, cancellationToken);
        if (link is null)
        {
            return null;
        }

        link.End(timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return link;
    }
}
