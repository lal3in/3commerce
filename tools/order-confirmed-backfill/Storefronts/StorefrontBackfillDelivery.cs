using System.Security.Cryptography;
using System.Text;
using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;
using ThreeCommerce.Fulfillment.Infrastructure.Consumers;
using ThreeCommerce.Payments.Infrastructure.Consumers;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill.Storefronts;

/// <summary>
/// Where a backfilled <c>StorefrontDuplicated</c> goes. Queue names are DERIVED with the shared formatter from the real
/// consumer types (ADR-0060), never typed by hand.
/// </summary>
public static class StorefrontBackfillQueues
{
    public const string BackfillHeaderValue = "storefront-duplicated (ADR-0060)";

    public static string QueueName(StorefrontBackfillTarget target) => target switch
    {
        StorefrontBackfillTarget.Payments => MassTransitExtensions.EndpointNameFormatter.Consumer<StorefrontDuplicatedConsumer>(),
        StorefrontBackfillTarget.Fulfillment => MassTransitExtensions.EndpointNameFormatter.Consumer<FulfillmentStorefrontDuplicatedConsumer>(),
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    /// <summary>A <c>queue:</c> address: <c>Send</c> to it reaches that one queue — no exchange fan-out, no other consumer.</summary>
    public static Uri Address(StorefrontBackfillTarget target) => new($"queue:{QueueName(target)}");

    /// <summary>The RabbitMQ client connection name of the target's process — what every consumer of the queue must be.</summary>
    public static string ExpectedConnectionName(StorefrontBackfillTarget target) => target switch
    {
        StorefrontBackfillTarget.Payments => "3commerce.Payments.Api",
        StorefrontBackfillTarget.Fulfillment => "3commerce.Fulfillment.Api",
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    /// <summary>A deterministic message id per (target, duplicated storefront): a repeat send is the SAME message.</summary>
    public static Guid MessageId(StorefrontBackfillTarget target, Guid newStorefrontId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"storefront-duplicated-backfill:{target}:{newStorefrontId:D}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// The event as Catalog published it: tenant, source, the duplicate, and the name it was created with. The source is
    /// the planner's (outcome-equivalent) choice — the consumers use it only to read the rows they copy.
    /// </summary>
    public static StorefrontDuplicated Rebuild(DuplicationVerdict verdict, StorefrontBackfillTarget target) =>
        verdict.Side(target) is { Outcome: SideOutcome.Missing, SourceStorefrontId: { } source }
            ? new StorefrontDuplicated(verdict.TenantId, source, verdict.TargetId, verdict.Name)
            : throw new InvalidOperationException($"storefront {verdict.TargetId} is not missing its {target} copy");
}

/// <summary>Sends rebuilt events point-to-point (<c>Send</c>, never <c>Publish</c>).</summary>
public sealed class StorefrontBackfillSender(ISendEndpointProvider endpoints)
{
    public async Task<int> SendAsync(
        StorefrontBackfillTarget target, IEnumerable<StorefrontDuplicated> messages, CancellationToken ct)
    {
        var endpoint = await endpoints.GetSendEndpoint(StorefrontBackfillQueues.Address(target));
        var sent = 0;
        foreach (var message in messages)
        {
            await endpoint.Send(message, context =>
            {
                context.MessageId = StorefrontBackfillQueues.MessageId(target, message.NewStorefrontId);
                context.Headers.Set(BackfillQueues.BackfillHeader, StorefrontBackfillQueues.BackfillHeaderValue);
            }, ct);
            sent++;
        }

        return sent;
    }
}
