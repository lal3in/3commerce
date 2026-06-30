using System.Text.Json;
using Microsoft.Extensions.Logging;
using ThreeCommerce.BuildingBlocks.Contracts.Streams;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

public sealed class StreamEventConsumerProcessor<TPayload>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IStreamEventHandler<TPayload> handler;
    private readonly IStreamProcessedEventStore processedEvents;
    private readonly IStreamDeadLetterSink deadLetters;
    private readonly ILogger<StreamEventConsumerProcessor<TPayload>> logger;

    public StreamEventConsumerProcessor(
        IStreamEventHandler<TPayload> handler,
        IStreamProcessedEventStore processedEvents,
        IStreamDeadLetterSink deadLetters,
        ILogger<StreamEventConsumerProcessor<TPayload>> logger)
    {
        this.handler = handler;
        this.processedEvents = processedEvents;
        this.deadLetters = deadLetters;
        this.logger = logger;
    }

    public async Task<StreamConsumerProcessResult> ProcessAsync(
        string topic,
        string key,
        long offset,
        string json,
        IReadOnlyDictionary<string, string> headers,
        string deadLetterTopic,
        CancellationToken cancellationToken = default)
    {
        StreamEventEnvelope<TPayload>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<StreamEventEnvelope<TPayload>>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            await SendToDeadLetterAsync(topic, key, offset, json, headers, deadLetterTopic, ex, cancellationToken);
            StreamMetrics.RecordConsumed(topic, StreamConsumerProcessResult.DeadLettered.ToString());
            return StreamConsumerProcessResult.DeadLettered;
        }

        if (envelope is null)
        {
            await SendToDeadLetterAsync(topic, key, offset, json, headers, deadLetterTopic, new InvalidOperationException("Envelope was null."), cancellationToken);
            StreamMetrics.RecordConsumed(topic, StreamConsumerProcessResult.DeadLettered.ToString());
            return StreamConsumerProcessResult.DeadLettered;
        }

        if (await processedEvents.HasProcessedAsync(envelope.EventId, cancellationToken))
        {
            logger.LogDebug("Skipping duplicate stream event {EventId} from {Topic} offset {Offset}", envelope.EventId, topic, offset);
            StreamMetrics.RecordConsumed(topic, StreamConsumerProcessResult.Duplicate.ToString());
            return StreamConsumerProcessResult.Duplicate;
        }

        try
        {
            await handler.HandleAsync(new StreamConsumedEvent<TPayload>(topic, key, offset, envelope, headers), cancellationToken);
            await processedEvents.MarkProcessedAsync(envelope.EventId, cancellationToken);
            StreamMetrics.RecordConsumed(topic, StreamConsumerProcessResult.Processed.ToString());
            return StreamConsumerProcessResult.Processed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await SendToDeadLetterAsync(topic, key, offset, json, headers, deadLetterTopic, ex, cancellationToken);
            StreamMetrics.RecordConsumed(topic, StreamConsumerProcessResult.DeadLettered.ToString());
            return StreamConsumerProcessResult.DeadLettered;
        }
    }

    private async Task SendToDeadLetterAsync(
        string topic,
        string key,
        long offset,
        string json,
        IReadOnlyDictionary<string, string> headers,
        string deadLetterTopic,
        Exception exception,
        CancellationToken cancellationToken)
    {
        logger.LogWarning(exception, "Dead-lettering stream event from {Topic} offset {Offset}", topic, offset);
        StreamMetrics.RecordDeadLettered(topic, exception.GetType().Name);
        await deadLetters.SendAsync(new StreamDeadLetterMessage(
            topic,
            string.IsNullOrWhiteSpace(deadLetterTopic) ? $"{topic}.dlq" : deadLetterTopic,
            key,
            offset,
            exception.GetType().Name,
            exception.Message,
            json,
            headers), cancellationToken);
    }
}

public enum StreamConsumerProcessResult
{
    Processed = 1,
    Duplicate = 2,
    DeadLettered = 3,
}
