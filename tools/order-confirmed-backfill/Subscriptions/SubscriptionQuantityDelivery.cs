using System.Security.Cryptography;
using System.Text;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;
using ThreeCommerce.BuildingBlocks.Infrastructure.Tenancy;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure;
using ThreeCommerce.Payments.Infrastructure.Consumers;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill.Subscriptions;

/// <summary>Payments' subscriptions of one tenant, read inside the RLS tenant scope (ADR-0024).</summary>
public sealed class PaymentsSubscriptionSource(PaymentsDbContext db, ITenantScope scope)
{
    public Task<List<Subscription>> SubscriptionsAsync(Guid tenantId, CancellationToken ct) =>
        scope.RunAsync(db, TenantContext.ForTenant(tenantId), () => db.Subscriptions.AsNoTracking()
            .Where(s => s.TenantId == tenantId).ToListAsync(ct), ct);
}

/// <summary>
/// Where a re-sent <see cref="SubscriptionRequested"/> goes: Payments' own queue, its name DERIVED with the shared
/// formatter from the real consumer type (ADR-0060), never typed by hand.
/// </summary>
public static class SubscriptionQuantityQueues
{
    public const string BackfillHeaderValue = "subscription-quantity (ADR-0057)";

    public const string ExpectedConnectionName = "3commerce.Payments.Api";

    public static string QueueName => MassTransitExtensions.EndpointNameFormatter.Consumer<SubscriptionRequestedConsumer>();

    /// <summary>A <c>queue:</c> address: <c>Send</c> to it reaches that one queue — no exchange fan-out, no other consumer.</summary>
    public static Uri Address => new($"queue:{QueueName}");

    /// <summary>A deterministic message id per subscription: a repeat send is the SAME message (Payments' inbox drops it).</summary>
    public static Guid MessageId(Guid subscriptionId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"subscription-quantity-backfill:{subscriptionId:D}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}

/// <summary>Sends rebuilt requests point-to-point (<c>Send</c>, never <c>Publish</c>).</summary>
public sealed class SubscriptionQuantitySender(ISendEndpointProvider endpoints)
{
    public async Task<int> SendAsync(IEnumerable<QuantityVerdict> verdicts, CancellationToken ct)
    {
        var endpoint = await endpoints.GetSendEndpoint(SubscriptionQuantityQueues.Address);
        var sent = 0;
        foreach (var verdict in verdicts)
        {
            var message = SubscriptionQuantityPlanner.Rebuild(verdict);
            await endpoint.Send(message, context =>
            {
                context.MessageId = SubscriptionQuantityQueues.MessageId(verdict.Subscription!.Id);
                context.Headers.Set(BackfillQueues.BackfillHeader, SubscriptionQuantityQueues.BackfillHeaderValue);
            }, ct);
            sent++;
        }

        return sent;
    }
}
