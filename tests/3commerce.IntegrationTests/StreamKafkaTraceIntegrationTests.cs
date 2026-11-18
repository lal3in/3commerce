using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ThreeCommerce.BuildingBlocks.Contracts.Streams;
using ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// ADR-0034 amendment (owner decision Q6) on a REAL Kafka broker: a fact staged inside a request is relayed later by
/// the stream outbox relay (no ambient trace), crosses Kafka through the real <see cref="KafkaStreamEventProducer"/>
/// and <see cref="KafkaStreamConsumerService{TPayload}"/>, and the consumer's span — and the handler running inside
/// it — joins the REQUEST's trace. Broker: the same bitnamilegacy/kafka:3.9 image as the dev infra (KRaft, one node).
/// </summary>
[Trait("Category", "Integration")]
[Collection(StreamTracingCollection.Name)]
public sealed class StreamKafkaTraceIntegrationTests : IAsyncLifetime
{
    private const string Topic = "commerce.orders";
    private static readonly ActivitySource Request = new("3commerce.tests.kafka-request");
    private readonly int hostPort = FreeTcpPort();
    private readonly IContainer kafka;
    private readonly List<Activity> stopped = [];
    private readonly ActivityListener listener;

    public StreamKafkaTraceIntegrationTests()
    {
        kafka = new ContainerBuilder("bitnamilegacy/kafka:3.9")
            .WithPortBinding(hostPort, 9092)
            .WithEnvironment("KAFKA_CFG_NODE_ID", "1")
            .WithEnvironment("KAFKA_CFG_PROCESS_ROLES", "broker,controller")
            .WithEnvironment("KAFKA_CFG_CONTROLLER_QUORUM_VOTERS", "1@localhost:9093")
            .WithEnvironment("KAFKA_CFG_LISTENERS", "PLAINTEXT://:9092,CONTROLLER://:9093")
            .WithEnvironment("KAFKA_CFG_ADVERTISED_LISTENERS", $"PLAINTEXT://localhost:{hostPort}")
            .WithEnvironment("KAFKA_CFG_LISTENER_SECURITY_PROTOCOL_MAP", "PLAINTEXT:PLAINTEXT,CONTROLLER:PLAINTEXT")
            .WithEnvironment("KAFKA_CFG_CONTROLLER_LISTENER_NAMES", "CONTROLLER")
            .WithEnvironment("KAFKA_CFG_AUTO_CREATE_TOPICS_ENABLE", "true")
            .WithEnvironment("KAFKA_CFG_OFFSETS_TOPIC_REPLICATION_FACTOR", "1")
            .WithEnvironment("KAFKA_CFG_TRANSACTION_STATE_LOG_REPLICATION_FACTOR", "1")
            .WithEnvironment("KAFKA_CFG_TRANSACTION_STATE_LOG_MIN_ISR", "1")
            .WithEnvironment("ALLOW_PLAINTEXT_LISTENER", "yes")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Kafka Server started"))
            .Build();

        listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is StreamTracing.ActivitySourceName or "3commerce.tests.kafka-request",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (stopped) stopped.Add(activity); },
        };
        ActivitySource.AddActivityListener(listener);
    }

    public Task InitializeAsync() => kafka.StartAsync();

    public async Task DisposeAsync()
    {
        listener.Dispose();
        await kafka.DisposeAsync();
    }

    [Fact]
    public async Task A_kafka_stream_consumer_joins_the_trace_of_the_request_that_committed_the_fact()
    {
        var bootstrap = $"localhost:{hostPort}";
        var store = new InMemoryStreamOutboxStore();
        ActivityTraceId traceId;
        ActivitySpanId requestSpan;
        using (var request = Request.StartActivity("POST /checkout", ActivityKind.Server)!)
        {
            (traceId, requestSpan) = (request.TraceId, request.SpanId);
            await new StreamOutboxStager(store).StageAsync(Topic, Envelope());
        }

        // Relay later, from a loop with no ambient trace — exactly like the hosted relay.
        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            using var producer = new KafkaStreamEventProducer(
                Options.Create(new StreamProducerOptions { Enabled = true, BootstrapServers = bootstrap, ClientId = "trace-it" }),
                NullLogger<KafkaStreamEventProducer>.Instance);
            var relayed = await new StreamOutboxRelay(store, producer, TimeProvider.System, NullLogger<StreamOutboxRelay>.Instance).RelayOnceAsync();
            Assert.Equal(1, relayed.Published);
        }
        finally
        {
            Activity.Current = previous;
        }

        var handler = new TraceRecordingHandler();
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IStreamEventHandler<KafkaTracedPayload>>(handler)
            .AddScoped<StreamEventConsumerProcessor<KafkaTracedPayload>>()
            .AddSingleton<IStreamProcessedEventStore, InMemoryStreamProcessedEventStore>()
            .AddSingleton<IStreamDeadLetterSink, InMemoryStreamDeadLetterSink>()
            .BuildServiceProvider();
        var consumer = new KafkaStreamConsumerService<KafkaTracedPayload>(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new StreamConsumerOptions
            {
                Enabled = true,
                Topic = Topic,
                ConsumerGroup = $"trace-it-{Guid.NewGuid():N}",
                BootstrapServers = bootstrap,
                DeadLetterTopic = $"{Topic}.dlq",
            }),
            services.GetRequiredService<ILogger<KafkaStreamConsumerService<KafkaTracedPayload>>>());

        await consumer.StartAsync(CancellationToken.None);
        try
        {
            var handledIn = await handler.Handled.Task.WaitAsync(TimeSpan.FromSeconds(90));
            Assert.Equal(traceId, handledIn);
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
        }

        lock (stopped)
        {
            var publish = Assert.Single(stopped, a => a.TraceId == traceId && a.Kind == ActivityKind.Producer);
            var process = Assert.Single(stopped, a => a.TraceId == traceId && a.Kind == ActivityKind.Consumer);
            Assert.Equal(requestSpan, publish.ParentSpanId);
            Assert.Equal(requestSpan, process.ParentSpanId);
            Assert.Equal($"{Topic} process", process.OperationName);
            Assert.NotNull(publish.GetTagItem("messaging.kafka.offset"));
        }
    }

    private static StreamEventEnvelope<KafkaTracedPayload> Envelope()
    {
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var orderId = Guid.CreateVersion7().ToString();
        return StreamEventEnvelope<KafkaTracedPayload>.Create(
            Guid.CreateVersion7(), "OrderConfirmed", 1, DateTimeOffset.UtcNow, "ordering", tenantId, orderId,
            StreamPartitionKeys.TenantAggregate(tenantId, orderId), StreamPrivacyClass.Internal, new KafkaTracedPayload("confirmed"));
    }

    private static int FreeTcpPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private sealed class TraceRecordingHandler : IStreamEventHandler<KafkaTracedPayload>
    {
        public TaskCompletionSource<ActivityTraceId> Handled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task HandleAsync(StreamConsumedEvent<KafkaTracedPayload> streamEvent, CancellationToken cancellationToken = default)
        {
            Handled.TrySetResult(Activity.Current?.TraceId ?? default);
            return Task.CompletedTask;
        }
    }
}

public sealed record KafkaTracedPayload(string Status);
