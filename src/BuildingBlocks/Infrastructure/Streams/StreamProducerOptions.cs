namespace ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

public sealed class StreamProducerOptions
{
    public const string SectionName = "EventStreaming";

    public bool Enabled { get; init; }

    public string BootstrapServers { get; init; } = "localhost:9092";

    public string ClientId { get; init; } = "3commerce";

    public string? SecurityProtocol { get; init; }

    public string? SaslMechanism { get; init; }

    public string? SaslUsername { get; init; }

    public string? SaslPassword { get; init; }
}
