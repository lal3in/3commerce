using Microsoft.Extensions.Logging;
using ThreeCommerce.BuildingBlocks.Contracts.Streams;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

public sealed class StreamOutboxRelay
{
    private readonly IStreamOutboxStore store;
    private readonly IStreamEventProducer producer;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<StreamOutboxRelay> logger;

    public StreamOutboxRelay(
        IStreamOutboxStore store,
        IStreamEventProducer producer,
        TimeProvider timeProvider,
        ILogger<StreamOutboxRelay> logger)
    {
        this.store = store;
        this.producer = producer;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<StreamOutboxRelayResult> RelayOnceAsync(int batchSize = 50, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var messages = await store.ClaimBatchAsync(batchSize, now, cancellationToken);
        var published = 0;
        var failed = 0;

        foreach (var message in messages)
        {
            try
            {
                var envelope = System.Text.Json.JsonSerializer.Deserialize<StreamEventEnvelope<object>>(message.PayloadJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
                    ?? throw new InvalidOperationException("Stream outbox payload did not contain an envelope.");

                await producer.PublishAsync(message.Topic, envelope, cancellationToken);
                message.MarkPublished(timeProvider.GetUtcNow());
                StreamMetrics.RecordRelayPublished(message.Topic);
                published++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var nextAttempt = timeProvider.GetUtcNow().Add(BackoffFor(message.PublishAttempts + 1));
                message.MarkFailed(ex.Message, nextAttempt);
                StreamMetrics.RecordRelayFailed(message.Topic);
                failed++;
                logger.LogWarning(ex, "Failed to relay stream outbox message {MessageId} to {Topic}", message.Id, message.Topic);
            }
        }

        await store.SaveChangesAsync(cancellationToken);
        return new StreamOutboxRelayResult(messages.Count, published, failed);
    }

    private static TimeSpan BackoffFor(int attempt) => TimeSpan.FromMinutes(Math.Min(Math.Pow(2, Math.Max(1, attempt)), 60));
}

public sealed record StreamOutboxRelayResult(int Claimed, int Published, int Failed);
