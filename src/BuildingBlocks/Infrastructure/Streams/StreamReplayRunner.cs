namespace ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

public sealed class StreamReplayRunner<TPayload>
{
    private readonly StreamEventConsumerProcessor<TPayload> processor;
    private readonly IStreamReplayWatermarkStore watermarks;

    public StreamReplayRunner(StreamEventConsumerProcessor<TPayload> processor, IStreamReplayWatermarkStore watermarks)
    {
        this.processor = processor;
        this.watermarks = watermarks;
    }

    public async Task<StreamReplayResult> ReplayAsync(
        string topic,
        string consumerGroup,
        IEnumerable<StreamReplayRecord> records,
        string? deadLetterTopic = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerGroup);

        var lastOffset = await watermarks.GetOffsetAsync(topic, consumerGroup, cancellationToken);
        var processed = 0;
        var duplicates = 0;
        var deadLettered = 0;
        var skippedByWatermark = 0;

        foreach (var record in records.OrderBy(x => x.Offset))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!record.Topic.Equals(topic, StringComparison.Ordinal))
                continue;

            if (lastOffset is not null && record.Offset <= lastOffset.Value)
            {
                skippedByWatermark++;
                continue;
            }

            var result = await processor.ProcessAsync(
                record.Topic,
                record.Key,
                record.Offset,
                record.Json,
                record.Headers,
                string.IsNullOrWhiteSpace(deadLetterTopic) ? $"{topic}.dlq" : deadLetterTopic,
                cancellationToken);

            switch (result)
            {
                case StreamConsumerProcessResult.Processed:
                    processed++;
                    break;
                case StreamConsumerProcessResult.Duplicate:
                    duplicates++;
                    break;
                case StreamConsumerProcessResult.DeadLettered:
                    deadLettered++;
                    break;
            }

            await watermarks.MarkOffsetAsync(topic, consumerGroup, record.Offset, cancellationToken);
            lastOffset = record.Offset;
        }

        return new StreamReplayResult(processed, duplicates, deadLettered, skippedByWatermark, lastOffset);
    }
}

public sealed record StreamReplayResult(int Processed, int Duplicates, int DeadLettered, int SkippedByWatermark, long? LastOffset);
