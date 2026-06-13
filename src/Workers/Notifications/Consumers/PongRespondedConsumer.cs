using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Ping;

namespace ThreeCommerce.Workers.Notifications.Consumers;

/// <summary>
/// Phase-1 spine terminus: logs the pong. Kept as the smoke-test flow.
/// No inbox here (worker has no DB) — log lines may duplicate on redelivery.
/// </summary>
public sealed class PongRespondedConsumer(ILogger<PongRespondedConsumer> logger) : IConsumer<PongResponded>
{
    public Task Consume(ConsumeContext<PongResponded> context)
    {
        logger.LogInformation("PONG received: PingId={PingId} RespondedBy={RespondedBy}",
            context.Message.PingId, context.Message.RespondedBy);
        return Task.CompletedTask;
    }
}
