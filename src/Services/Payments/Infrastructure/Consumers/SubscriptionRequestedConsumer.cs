using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;

namespace ThreeCommerce.Payments.Infrastructure.Consumers;

/// <summary>Sets up a subscription when a confirmed order has a recurring line (mt7_3). Idempotent.</summary>
public sealed class SubscriptionRequestedConsumer(SubscriptionService subscriptions) : IConsumer<SubscriptionRequested>
{
    public Task Consume(ConsumeContext<SubscriptionRequested> context) =>
        subscriptions.StartAsync(context.Message, context.CancellationToken);
}
