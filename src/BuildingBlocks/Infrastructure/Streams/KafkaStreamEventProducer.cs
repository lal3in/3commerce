using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThreeCommerce.BuildingBlocks.Contracts.Streams;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

public sealed class KafkaStreamEventProducer : IStreamEventProducer, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IProducer<string, string> producer;
    private readonly ILogger<KafkaStreamEventProducer> logger;

    public KafkaStreamEventProducer(IOptions<StreamProducerOptions> options, ILogger<KafkaStreamEventProducer> logger)
    {
        this.logger = logger;
        var value = options.Value;
        var config = new ProducerConfig
        {
            BootstrapServers = value.BootstrapServers,
            ClientId = value.ClientId,
            EnableIdempotence = true,
            Acks = Acks.All,
        };

        if (!string.IsNullOrWhiteSpace(value.SecurityProtocol) && Enum.TryParse<SecurityProtocol>(value.SecurityProtocol, ignoreCase: true, out var securityProtocol))
            config.SecurityProtocol = securityProtocol;
        if (!string.IsNullOrWhiteSpace(value.SaslMechanism) && Enum.TryParse<SaslMechanism>(value.SaslMechanism, ignoreCase: true, out var mechanism))
            config.SaslMechanism = mechanism;
        if (!string.IsNullOrWhiteSpace(value.SaslUsername))
            config.SaslUsername = value.SaslUsername;
        if (!string.IsNullOrWhiteSpace(value.SaslPassword))
            config.SaslPassword = value.SaslPassword;

        producer = new ProducerBuilder<string, string>(config).Build();
    }

    public async Task<StreamPublishResult> PublishAsync<TPayload>(
        string topic,
        StreamEventEnvelope<TPayload> envelope,
        CancellationToken cancellationToken = default)
    {
        StreamPrivacyGuard.ValidateForPublication(topic, envelope);

        var message = new Message<string, string>
        {
            Key = envelope.PartitionKey,
            Value = JsonSerializer.Serialize(envelope, JsonOptions),
            Headers = ToKafkaHeaders(StreamHeaders.FromEnvelope(envelope)),
        };

        var result = await producer.ProduceAsync(topic, message, cancellationToken);
        StreamMetrics.RecordPublished(result.Topic);
        logger.LogInformation(
            "Published stream event {EventType} v{EventVersion} to {Topic} partition {Partition} offset {Offset}",
            envelope.EventType,
            envelope.EventVersion,
            result.Topic,
            result.Partition.Value,
            result.Offset.Value);

        return new StreamPublishResult(result.Topic, envelope.PartitionKey, result.Offset.Value);
    }

    public void Dispose()
    {
        producer.Flush(TimeSpan.FromSeconds(5));
        producer.Dispose();
    }

    private static Headers ToKafkaHeaders(IReadOnlyDictionary<string, string> source)
    {
        var headers = new Headers();
        foreach (var (key, value) in source)
        {
            headers.Add(key, Encoding.UTF8.GetBytes(value));
        }

        return headers;
    }
}
