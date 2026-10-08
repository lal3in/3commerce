using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Tenancy;
using ThreeCommerce.Fulfillment.Infrastructure;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure;
using ThreeCommerce.Workers.Notifications.Email;
using ThreeCommerce.Workers.Notifications.Infrastructure;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill;

/// <summary>
/// Runs a read inside a tenant scope. Production uses <see cref="RlsTenantScope"/> — the transaction-local
/// <c>app.tenant_id</c> settings PostgreSQL RLS reads (ADR-0024) — so the tool keeps working, and keeps reading only
/// what it should, if these tables gain RLS policies. Tests on the EF in-memory provider pass straight through.
/// </summary>
public interface ITenantScope
{
    public Task<T> RunAsync<T>(DbContext db, TenantContext context, Func<Task<T>> work, CancellationToken ct);
}

public sealed class RlsTenantScope : ITenantScope
{
    public Task<T> RunAsync<T>(DbContext db, TenantContext context, Func<Task<T>> work, CancellationToken ct) =>
        db.RunInTenantScopeAsync(context, work, ct);
}

/// <summary>Ordering's confirmed orders — the source of truth the backfilled events are rebuilt from.</summary>
public sealed class OrderSource(OrderingDbContext db, ITenantScope scope)
{
    /// <summary>Every tenant with a confirmed-family order. Enumerating across tenants is a platform-scope read.</summary>
    public Task<List<Guid>> TenantsAsync(CancellationToken ct) =>
        scope.RunAsync(db, TenantContext.Platform(), () => db.Orders.AsNoTracking()
            .Where(o => BackfillPlanner.ConfirmedFamily.Contains(o.Status))
            .Select(o => o.TenantId)
            .Distinct()
            .OrderBy(t => t)
            .ToListAsync(ct), ct);

    /// <summary>
    /// One tenant's confirmed-family orders WITH their lines, loaded exactly like the live confirmation path loads an
    /// existing order (<c>Include(o =&gt; o.Lines)</c>), so <see cref="OrderConfirmedFactory"/> sees the same aggregate.
    /// </summary>
    public Task<List<Order>> ConfirmedOrdersAsync(Guid tenantId, CancellationToken ct) =>
        scope.RunAsync(db, TenantContext.ForTenant(tenantId), () => db.Orders.AsNoTracking()
            .Include(o => o.Lines)
            .Where(o => o.TenantId == tenantId && BackfillPlanner.ConfirmedFamily.Contains(o.Status))
            .ToListAsync(ct), ct);
}

/// <summary>Fulfillment's own record of having handled an order: a shipment, or a captured held order.</summary>
public sealed class FulfillmentSource(FulfillmentDbContext db, ITenantScope scope)
{
    /// <summary>
    /// The orders (of <paramref name="orderIds"/>) Fulfillment already has a <c>Shipment</c> or <c>HeldOrder</c> for —
    /// the two checks <c>FulfillmentOrderConfirmedConsumer</c> makes before acting. Matched by order id, not tenant,
    /// so a row carrying an unexpected tenant still counts as handled (never re-fulfilled).
    /// </summary>
    public Task<HashSet<Guid>> SeenAsync(Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken ct)
    {
        var ids = orderIds.ToList();
        return scope.RunAsync(db, TenantContext.ForTenant(tenantId), async () =>
        {
            var shipped = await db.Shipments.AsNoTracking().Where(s => ids.Contains(s.OrderId)).Select(s => s.OrderId).Distinct().ToListAsync(ct);
            var held = await db.HeldOrders.AsNoTracking().Where(h => ids.Contains(h.OrderId)).Select(h => h.OrderId).ToListAsync(ct);
            return shipped.Concat(held).ToHashSet();
        }, ct);
    }
}

/// <summary>
/// The Notifications delivery log. It has no tenant column (it is the worker's operational log, mc_proc_4), so it
/// is read unscoped.
/// </summary>
public sealed class DeliverySource(NotificationsDbContext db)
{
    /// <summary>The subject the live template gives a confirmation email — legacy rows are recognised by it.</summary>
    public static string OrderConfirmedSubject { get; } =
        new EmailTemplates(string.Empty).OrderConfirmed("x@example.invalid", Guid.Empty, 0, "AUD").Subject;

    /// <summary>
    /// Whether the worker has applied <c>DeliveryReference</c> (it migrates on start). Without the column the worker
    /// does not record which order an email was for, so a notifications backfill could not be re-run safely.
    /// </summary>
    public async Task<bool> HasReferenceColumnAsync(CancellationToken ct) =>
        !db.Database.IsRelational() // the EF in-memory test provider has no catalog; its model has the column
        || await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM information_schema.columns
            WHERE table_schema = 'notifications' AND table_name = 'deliveries' AND column_name = 'Reference'
            """).SingleAsync(ct) == 1;

    /// <summary>The first row of the log, of any kind: before it there is no evidence about any email.</summary>
    public Task<DateTimeOffset?> LogStartAsync(CancellationToken ct) =>
        db.Deliveries.AsNoTracking().MinAsync(d => (DateTimeOffset?)d.OccurredAt, ct);

    public Task<List<DeliveryFact>> OrderConfirmationDeliveriesAsync(CancellationToken ct)
    {
        var subject = OrderConfirmedSubject;
        const string prefix = NotificationReferences.OrderConfirmedPrefix;
        return db.Deliveries.AsNoTracking()
            .Where(d => d.Subject == subject || (d.Reference != null && d.Reference.StartsWith(prefix)))
            .Select(d => new DeliveryFact(d.Recipient, d.Status, d.OccurredAt, d.Reference))
            .ToListAsync(ct);
    }
}
