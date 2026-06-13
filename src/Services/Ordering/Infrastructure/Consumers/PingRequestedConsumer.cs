using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Ping;

namespace ThreeCommerce.Ordering.Infrastructure.Consumers;

/// <summary>
/// Phase-1 spine consumer. Endpoint-level EF inbox (BuildingBlocks callback) makes
/// this idempotent: duplicate deliveries of the same message produce one pong.
/// </summary>
public class PingRequestedConsumer : IConsumer<PingRequested>
{
    public async Task Consume(ConsumeContext<PingRequested> context)
    {
        await context.Publish(new PongResponded(context.Message.PingId, "ordering"));
    }
}
