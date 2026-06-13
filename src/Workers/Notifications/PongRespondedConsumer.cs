using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Ping;

namespace ThreeCommerce.Workers.Notifications;

/// <summary>
/// Phase-1 spine terminus: logs the pong. Replaced by real email sending in Phase 2.
/// No inbox here (worker has no DB) — log lines may duplicate on redelivery, which is fine.
/// </summary>
public class PongRespondedConsumer(ILogger<PongRespondedConsumer> logger) : IConsumer<PongResponded>
{
    public Task Consume(ConsumeContext<PongResponded> context)
    {
        logger.LogInformation("PONG received: PingId={PingId} RespondedBy={RespondedBy}",
            context.Message.PingId, context.Message.RespondedBy);
        return Task.CompletedTask;
    }
}
