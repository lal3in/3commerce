using System.Security.Cryptography;
using System.Text;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;
using ThreeCommerce.BuildingBlocks.Infrastructure.Tenancy;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure;
using ThreeCommerce.Support.Infrastructure;
using ThreeCommerce.Support.Infrastructure.Consumers;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill.SupportOwners;

/// <summary>Support's order copies that have no owner. Support's tables carry no tenant and no RLS, so this is unscoped.</summary>
public sealed class SupportSnapshotSource(SupportDbContext db)
{
    public Task<List<Guid>> OwnerlessAsync(CancellationToken ct) =>
        db.OrderSnapshots.AsNoTracking().Where(o => o.UserId == null).Select(o => o.OrderId).ToListAsync(ct);
}

/// <summary>Ordering's view of those orders, read inside the RLS scope (ADR-0024).</summary>
public sealed class OrderOwnerSource(OrderingDbContext db, ITenantScope scope)
{
    /// <summary>Id, tenant and user of each named order — a cross-tenant lookup by id, so a platform-scope read.</summary>
    public Task<List<OrderOwnerFact>> OwnersAsync(IReadOnlyCollection<Guid> orderIds, CancellationToken ct)
    {
        var ids = orderIds.ToList();
        return scope.RunAsync(db, TenantContext.Platform(), () => db.Orders.AsNoTracking()
            .Where(o => ids.Contains(o.Id))
            .Select(o => new OrderOwnerFact(o.Id, o.TenantId, o.UserId))
            .ToListAsync(ct), ct);
    }

    /// <summary>One tenant's named orders WITH their lines — the aggregate <see cref="OrderConfirmedFactory"/> rebuilds from.</summary>
    public Task<List<Order>> OrdersAsync(Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken ct)
    {
        var ids = orderIds.ToList();
        return scope.RunAsync(db, TenantContext.ForTenant(tenantId), () => db.Orders.AsNoTracking()
            .Include(o => o.Lines)
            .Where(o => o.TenantId == tenantId && ids.Contains(o.Id))
            .ToListAsync(ct), ct);
    }
}

/// <summary>
/// Where the rebuilt event goes: Support's own snapshot queue, its name DERIVED with the shared formatter from the real
/// consumer type (ADR-0060), never typed by hand. A <c>Send</c> to it reaches only Support — Fulfillment, Notifications
/// and the rest never see the copy.
/// </summary>
public static class SupportOrderOwnerQueues
{
    public const string BackfillHeaderValue = "support-order-owner (support_order_ownership)";

    public const string ExpectedConnectionName = "3commerce.Support.Api";

    public static string QueueName => MassTransitExtensions.EndpointNameFormatter.Consumer<OrderSnapshotConsumer>();

    public static Uri Address => new($"queue:{QueueName}");

    /// <summary>A deterministic message id per order: a repeat send is the SAME message (Support's inbox drops it), and the
    /// consumer only ever fills a missing owner anyway.</summary>
    public static Guid MessageId(Guid orderId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"support-order-owner-backfill:{orderId:D}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}

/// <summary>Sends the rebuilt events point-to-point (<c>Send</c>, never <c>Publish</c>).</summary>
public sealed class SupportOrderOwnerSender(ISendEndpointProvider endpoints)
{
    public async Task<int> SendAsync(IEnumerable<OrderConfirmed> messages, CancellationToken ct)
    {
        var endpoint = await endpoints.GetSendEndpoint(SupportOrderOwnerQueues.Address);
        var sent = 0;
        foreach (var message in messages)
        {
            await endpoint.Send(message, context =>
            {
                context.MessageId = SupportOrderOwnerQueues.MessageId(message.OrderId);
                context.Headers.Set(BackfillQueues.BackfillHeader, SupportOrderOwnerQueues.BackfillHeaderValue);
            }, ct);
            sent++;
        }

        return sent;
    }
}
