namespace ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

public sealed class StreamConsumerOptions
{
    public string Topic { get; init; } = string.Empty;

    public string ConsumerGroup { get; init; } = string.Empty;

    public string BootstrapServers { get; init; } = "localhost:9092";

    public string ClientId { get; init; } = "3commerce-consumer";

    public bool Enabled { get; init; }

    public string DeadLetterTopic { get; init; } = string.Empty;

    public int MaxPoisonAttempts { get; init; } = 3;

    public string? SecurityProtocol { get; init; }

    public string? SaslMechanism { get; init; }

    public string? SaslUsername { get; init; }

    public string? SaslPassword { get; init; }
}
