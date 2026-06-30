using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

public sealed class KafkaStreamConsumerService<TPayload> : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly StreamConsumerOptions options;
    private readonly ILogger<KafkaStreamConsumerService<TPayload>> logger;

    public KafkaStreamConsumerService(
        IServiceScopeFactory scopeFactory,
        IOptions<StreamConsumerOptions> options,
        ILogger<KafkaStreamConsumerService<TPayload>> logger)
    {
        this.scopeFactory = scopeFactory;
        this.options = options.Value;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Kafka stream consumer for {Topic} is disabled", options.Topic);
            return;
        }

        using var consumer = BuildConsumer();
        consumer.Subscribe(options.Topic);
        logger.LogInformation("Kafka stream consumer {ConsumerGroup} subscribed to {Topic}", options.ConsumerGroup, options.Topic);

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string>? result = null;
            try
            {
                result = consumer.Consume(stoppingToken);
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<StreamEventConsumerProcessor<TPayload>>();
                await processor.ProcessAsync(
                    result.Topic,
                    result.Message.Key ?? string.Empty,
                    result.Offset.Value,
                    result.Message.Value,
                    ReadHeaders(result.Message.Headers),
                    options.DeadLetterTopic,
                    stoppingToken);
                consumer.Commit(result);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Kafka stream consumer failed for {Topic} offset {Offset}", options.Topic, result?.Offset.Value);
            }
        }

        consumer.Close();
    }

    private IConsumer<string, string> BuildConsumer()
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = options.BootstrapServers,
            ClientId = options.ClientId,
            GroupId = options.ConsumerGroup,
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
        };

        if (!string.IsNullOrWhiteSpace(options.SecurityProtocol) && Enum.TryParse<SecurityProtocol>(options.SecurityProtocol, ignoreCase: true, out var securityProtocol))
            config.SecurityProtocol = securityProtocol;
        if (!string.IsNullOrWhiteSpace(options.SaslMechanism) && Enum.TryParse<SaslMechanism>(options.SaslMechanism, ignoreCase: true, out var mechanism))
            config.SaslMechanism = mechanism;
        if (!string.IsNullOrWhiteSpace(options.SaslUsername))
            config.SaslUsername = options.SaslUsername;
        if (!string.IsNullOrWhiteSpace(options.SaslPassword))
            config.SaslPassword = options.SaslPassword;

        return new ConsumerBuilder<string, string>(config).Build();
    }

    private static IReadOnlyDictionary<string, string> ReadHeaders(Headers? headers)
    {
        var values = new Dictionary<string, string>();
        if (headers is null)
            return values;

        foreach (var header in headers)
        {
            values[header.Key] = Encoding.UTF8.GetString(header.GetValueBytes());
        }

        return values;
    }
}
