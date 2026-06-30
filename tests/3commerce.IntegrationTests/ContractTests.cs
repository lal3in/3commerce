using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ThreeCommerce.BuildingBlocks.Contracts.Ping;
using ThreeCommerce.BuildingBlocks.Contracts.Streams;
using ThreeCommerce.BuildingBlocks.Infrastructure.Scheduling;
using ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Unit-lane guard: contracts must be records with value equality — inbox dedup and
/// test assertions rely on it. Fails if someone converts a contract to a class.
/// </summary>
public class ContractTests
{
    [Fact]
    public void Contracts_have_value_equality()
    {
        var id = Guid.CreateVersion7();
        var at = DateTimeOffset.UtcNow;

        Assert.Equal(new PingRequested(id, at), new PingRequested(id, at));
        Assert.Equal(new PongResponded(id, "ordering"), new PongResponded(id, "ordering"));
        Assert.NotEqual(new PongResponded(id, "ordering"), new PongResponded(id, "other"));
    }

    [Fact]
    public void Stream_envelope_sets_schema_version_and_normalizes_metadata()
    {
        var tenantId = Guid.CreateVersion7();
        var orderId = Guid.CreateVersion7();
        var envelope = StreamEventEnvelope<object>.Create(
            Guid.CreateVersion7(),
            "OrderConfirmed",
            1,
            DateTimeOffset.UtcNow,
            "Ordering",
            tenantId,
            orderId.ToString(),
            StreamPartitionKeys.TenantAggregate(tenantId, orderId),
            StreamPrivacyClass.Internal,
            new { orderId },
            correlationId: " checkout-flow ");

        Assert.Equal(StreamEventEnvelope<object>.CurrentSchemaVersion, envelope.SchemaVersion);
        Assert.Equal("ordering", envelope.SourceService);
        Assert.Equal($"{tenantId}:{orderId}", envelope.PartitionKey);
        Assert.Equal("checkout-flow", envelope.CorrelationId);
        Assert.Equal(StreamPrivacyClass.Internal, envelope.PrivacyClass);
    }

    [Fact]
    public void Stream_envelope_rejects_missing_required_metadata()
    {
        Assert.Throws<ArgumentException>(() => StreamEventEnvelope<object>.Create(
            Guid.CreateVersion7(),
            " ",
            1,
            DateTimeOffset.UtcNow,
            "ordering",
            Guid.CreateVersion7(),
            null,
            "tenant:aggregate",
            StreamPrivacyClass.Internal,
            new { }));

        Assert.Throws<ArgumentOutOfRangeException>(() => StreamEventEnvelope<object>.Create(
            Guid.CreateVersion7(),
            "OrderConfirmed",
            0,
            DateTimeOffset.UtcNow,
            "ordering",
            Guid.CreateVersion7(),
            null,
            "tenant:aggregate",
            StreamPrivacyClass.Internal,
            new { }));
    }

    [Fact]
    public void Stream_topic_catalog_uses_stable_domain_scoped_names()
    {
        Assert.Equal("commerce.orders", StreamTopics.CommerceOrders);
        Assert.Equal("payments.ledger", StreamTopics.PaymentsLedger);
        Assert.Equal("audit.entries", StreamTopics.AuditEntries);
        Assert.DoesNotContain("command", StreamTopics.CommerceOrders, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fake_stream_producer_records_json_payload_and_headers()
    {
        var tenantId = Guid.CreateVersion7();
        var orderId = Guid.CreateVersion7();
        var envelope = StreamEventEnvelope<object>.Create(
            Guid.CreateVersion7(),
            "OrderConfirmed",
            1,
            DateTimeOffset.UtcNow,
            "ordering",
            tenantId,
            orderId.ToString(),
            StreamPartitionKeys.TenantAggregate(tenantId, orderId),
            StreamPrivacyClass.Internal,
            new { orderId });
        var producer = new FakeStreamEventProducer();

        var result = await producer.PublishAsync(StreamTopics.CommerceOrders, envelope);

        var published = Assert.Single(producer.Published);
        Assert.Equal(StreamTopics.CommerceOrders, result.Topic);
        Assert.Equal(envelope.PartitionKey, published.Key);
        Assert.Contains("OrderConfirmed", published.EventJson);
        Assert.Equal(envelope.EventId.ToString(), published.Headers["event-id"]);
        Assert.Equal("Internal", published.Headers["privacy-class"]);
    }

    [Fact]
    public async Task Stream_consumer_processor_is_idempotent_by_event_id()
    {
        var envelope = StreamEventEnvelope<TestStreamPayload>.Create(
            Guid.CreateVersion7(),
            "OrderConfirmed",
            1,
            DateTimeOffset.UtcNow,
            "ordering",
            Guid.CreateVersion7(),
            Guid.CreateVersion7().ToString(),
            "tenant:order",
            StreamPrivacyClass.Internal,
            new TestStreamPayload("confirmed"));
        var handler = new RecordingStreamHandler();
        var processor = new StreamEventConsumerProcessor<TestStreamPayload>(
            handler,
            new InMemoryStreamProcessedEventStore(),
            new InMemoryStreamDeadLetterSink(),
            NullLogger<StreamEventConsumerProcessor<TestStreamPayload>>.Instance);
        var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var first = await processor.ProcessAsync(StreamTopics.CommerceOrders, envelope.PartitionKey, 1, json, new Dictionary<string, string>(), "commerce.orders.dlq");
        var second = await processor.ProcessAsync(StreamTopics.CommerceOrders, envelope.PartitionKey, 1, json, new Dictionary<string, string>(), "commerce.orders.dlq");

        Assert.Equal(StreamConsumerProcessResult.Processed, first);
        Assert.Equal(StreamConsumerProcessResult.Duplicate, second);
        Assert.Equal(1, handler.HandledCount);
    }

    [Fact]
    public async Task Stream_consumer_processor_dead_letters_invalid_json()
    {
        var deadLetters = new InMemoryStreamDeadLetterSink();
        var processor = new StreamEventConsumerProcessor<TestStreamPayload>(
            new RecordingStreamHandler(),
            new InMemoryStreamProcessedEventStore(),
            deadLetters,
            NullLogger<StreamEventConsumerProcessor<TestStreamPayload>>.Instance);

        var result = await processor.ProcessAsync(StreamTopics.CommerceOrders, "key", 42, "not-json", new Dictionary<string, string>(), "commerce.orders.dlq");

        Assert.Equal(StreamConsumerProcessResult.DeadLettered, result);
        var deadLetter = Assert.Single(deadLetters.Messages);
        Assert.Equal("commerce.orders.dlq", deadLetter.DeadLetterTopic);
        Assert.Equal("JsonException", deadLetter.ErrorType);
    }

    [Fact]
    public async Task Stream_outbox_stages_committed_envelope_without_publishing_directly()
    {
        var store = new InMemoryStreamOutboxStore();
        var stager = new StreamOutboxStager(store);
        var tenantId = Guid.CreateVersion7();
        var envelope = StreamEventEnvelope<TestStreamPayload>.Create(
            Guid.CreateVersion7(),
            "OrderConfirmed",
            1,
            DateTimeOffset.UtcNow,
            "ordering",
            tenantId,
            Guid.CreateVersion7().ToString(),
            StreamPartitionKeys.Tenant(tenantId),
            StreamPrivacyClass.Internal,
            new TestStreamPayload("confirmed"));

        var message = await stager.StageAsync(StreamTopics.CommerceOrders, envelope);

        Assert.Single(store.Messages);
        Assert.Equal(StreamTopics.CommerceOrders, message.Topic);
        Assert.Equal(envelope.EventId, System.Text.Json.JsonSerializer.Deserialize<StreamEventEnvelope<TestStreamPayload>>(message.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.EventId);
        Assert.Null(message.PublishedAt);
    }

    [Fact]
    public async Task Stream_outbox_relay_publishes_due_rows_and_marks_them_published()
    {
        var store = new InMemoryStreamOutboxStore();
        var producer = new FakeStreamEventProducer();
        var relay = new StreamOutboxRelay(store, producer, TimeProvider.System, NullLogger<StreamOutboxRelay>.Instance);
        var envelope = StreamEventEnvelope<TestStreamPayload>.Create(
            Guid.CreateVersion7(),
            "OrderConfirmed",
            1,
            DateTimeOffset.UtcNow,
            "ordering",
            Guid.CreateVersion7(),
            Guid.CreateVersion7().ToString(),
            "tenant:order",
            StreamPrivacyClass.Internal,
            new TestStreamPayload("confirmed"));
        await new StreamOutboxStager(store).StageAsync(StreamTopics.CommerceOrders, envelope);

        var result = await relay.RelayOnceAsync();

        Assert.Equal(new StreamOutboxRelayResult(1, 1, 0), result);
        Assert.NotNull(Assert.Single(store.Messages).PublishedAt);
        Assert.Single(producer.Published);
    }

    [Fact]
    public async Task Stream_outbox_relay_records_failure_and_backoff_without_marking_published()
    {
        var store = new InMemoryStreamOutboxStore();
        var relay = new StreamOutboxRelay(store, new ThrowingStreamProducer(), TimeProvider.System, NullLogger<StreamOutboxRelay>.Instance);
        var envelope = StreamEventEnvelope<TestStreamPayload>.Create(
            Guid.CreateVersion7(),
            "OrderConfirmed",
            1,
            DateTimeOffset.UtcNow,
            "ordering",
            Guid.CreateVersion7(),
            Guid.CreateVersion7().ToString(),
            "tenant:order",
            StreamPrivacyClass.Internal,
            new TestStreamPayload("confirmed"));
        await new StreamOutboxStager(store).StageAsync(StreamTopics.CommerceOrders, envelope);

        var result = await relay.RelayOnceAsync();

        var message = Assert.Single(store.Messages);
        Assert.Equal(new StreamOutboxRelayResult(1, 0, 1), result);
        Assert.Null(message.PublishedAt);
        Assert.Equal(1, message.PublishAttempts);
        Assert.Contains("boom", message.LastError);
    }

    [Fact]
    public async Task Stream_privacy_guard_rejects_restricted_payloads_and_missing_tenant_metadata()
    {
        var restricted = StreamEventEnvelope<TestStreamPayload>.Create(
            Guid.CreateVersion7(),
            "RestrictedFact",
            1,
            DateTimeOffset.UtcNow,
            "payments",
            Guid.CreateVersion7(),
            null,
            "tenant:secret",
            StreamPrivacyClass.Restricted,
            new TestStreamPayload("secret"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new FakeStreamEventProducer().PublishAsync(StreamTopics.PaymentsLedger, restricted));

        var missingTenant = StreamEventEnvelope<TestStreamPayload>.Create(
            Guid.CreateVersion7(),
            "TenantFact",
            1,
            DateTimeOffset.UtcNow,
            "ordering",
            null,
            null,
            "platform",
            StreamPrivacyClass.Internal,
            new TestStreamPayload("missing-tenant"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new StreamOutboxStager(new InMemoryStreamOutboxStore()).StageAsync(StreamTopics.CommerceOrders, missingTenant));
    }

    [Fact]
    public async Task Commerce_stream_fact_factory_uses_owner_topics_keys_and_internal_privacy()
    {
        var tenantId = Guid.CreateVersion7();
        var orderId = Guid.CreateVersion7();
        var journalEntryId = Guid.CreateVersion7();
        var offerId = Guid.CreateVersion7();
        var usageId = Guid.CreateVersion7();
        var customerId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryStreamOutboxStore();
        var stager = new StreamOutboxStager(store);

        var order = CommerceStreamFactFactory.OrderLifecycle(tenantId, orderId, "Confirmed", 12_34, "USD", now);
        var ledger = CommerceStreamFactFactory.LedgerPosted(tenantId, journalEntryId, "OrderPayment", 12_34, 12_34, "USD", now, "Order", orderId.ToString());
        var offer = CommerceStreamFactFactory.OfferChanged(tenantId, offerId, Guid.CreateVersion7(), null, Guid.CreateVersion7(), "Physical", "Warehouse", 12_34, "USD", now);
        var usage = CommerceStreamFactFactory.UsageRecorded(tenantId, usageId, customerId, "api-calls", 10, now, "meter-window-1");

        await stager.StageAsync(StreamTopics.CommerceOrders, order);
        await stager.StageAsync(StreamTopics.PaymentsLedger, ledger);
        await stager.StageAsync(StreamTopics.CatalogOffers, offer);
        await stager.StageAsync(StreamTopics.UsageRecords, usage);

        Assert.All(store.Messages, message => Assert.Null(message.PublishedAt));
        Assert.Contains(store.Messages, message => message.Topic == StreamTopics.CommerceOrders && message.Key == StreamPartitionKeys.TenantAggregate(tenantId, orderId));
        Assert.Contains(store.Messages, message => message.Topic == StreamTopics.PaymentsLedger && message.Key == StreamPartitionKeys.TenantAggregate(tenantId, journalEntryId));
        Assert.Contains(store.Messages, message => message.Topic == StreamTopics.CatalogOffers && message.Key == StreamPartitionKeys.TenantAggregate(tenantId, offerId));
        Assert.Contains(store.Messages, message => message.Topic == StreamTopics.UsageRecords && message.Key == StreamPartitionKeys.TenantAggregate(tenantId, customerId));
        Assert.All(store.Messages, message => Assert.Contains("Internal", message.HeadersJson));
        Assert.DoesNotContain("card", string.Join('\n', store.Messages.Select(x => x.PayloadJson)), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", string.Join('\n', store.Messages.Select(x => x.PayloadJson)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Persistent_scheduler_requires_connection_string_when_enabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Quartz:PersistentStoreEnabled"] = "true",
            })
            .Build();
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddScheduledJobs(configuration, _ => { }));

        Assert.Contains("Quartz:ConnectionString", ex.Message);
    }

    [Fact]
    public void Scheduler_registers_typed_jobs()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddScheduledJobs(_ => _.Add<NoopScheduledJob>("noop", "0 0/5 * * * ?"));

        using var provider = services.BuildServiceProvider();
        Assert.Contains(provider.GetServices<IScheduledJob>(), job => job.Name == "noop");
    }

    [Fact]
    public async Task Stream_relay_recovers_after_transient_broker_failure_without_duplicate_publish()
    {
        var store = new InMemoryStreamOutboxStore();
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var envelope = StreamEventEnvelope<TestStreamPayload>.Create(
            Guid.CreateVersion7(),
            "OrderConfirmed",
            1,
            time.GetUtcNow(),
            "ordering",
            Guid.CreateVersion7(),
            null,
            "tenant:order",
            StreamPrivacyClass.Internal,
            new TestStreamPayload("confirmed"));
        await new StreamOutboxStager(store).StageAsync(StreamTopics.CommerceOrders, envelope);

        var failed = await new StreamOutboxRelay(store, new ThrowingStreamProducer(), time, NullLogger<StreamOutboxRelay>.Instance).RelayOnceAsync();
        time.Advance(TimeSpan.FromMinutes(3));
        var producer = new FakeStreamEventProducer();
        var recovered = await new StreamOutboxRelay(store, producer, time, NullLogger<StreamOutboxRelay>.Instance).RelayOnceAsync();
        var final = await new StreamOutboxRelay(store, producer, time, NullLogger<StreamOutboxRelay>.Instance).RelayOnceAsync();

        Assert.Equal(new StreamOutboxRelayResult(1, 0, 1), failed);
        Assert.Equal(new StreamOutboxRelayResult(1, 1, 0), recovered);
        Assert.Equal(new StreamOutboxRelayResult(0, 0, 0), final);
        Assert.Single(producer.Published);
        Assert.NotNull(Assert.Single(store.Messages).PublishedAt);
    }

    [Fact]
    public async Task Stream_consumer_tolerates_additive_unknown_schema_fields()
    {
        var envelope = StreamEventEnvelope<TestStreamPayload>.Create(
            Guid.CreateVersion7(),
            "OrderConfirmed",
            1,
            DateTimeOffset.UtcNow,
            "ordering",
            Guid.CreateVersion7(),
            null,
            "tenant:order",
            StreamPrivacyClass.Internal,
            new TestStreamPayload("confirmed"));
        var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json = json.Insert(json.Length - 1, ",\"newOptionalField\":\"ignored\"");
        var handler = new RecordingStreamHandler();
        var processor = new StreamEventConsumerProcessor<TestStreamPayload>(
            handler,
            new InMemoryStreamProcessedEventStore(),
            new InMemoryStreamDeadLetterSink(),
            NullLogger<StreamEventConsumerProcessor<TestStreamPayload>>.Instance);

        var result = await processor.ProcessAsync(StreamTopics.CommerceOrders, envelope.PartitionKey, 99, json, new Dictionary<string, string>(), "commerce.orders.dlq");

        Assert.Equal(StreamConsumerProcessResult.Processed, result);
        Assert.Equal(1, handler.HandledCount);
    }

    [Fact]
    public async Task Replay_runner_rebuilds_read_model_idempotently_and_resumes_from_watermark()
    {
        var tenantId = Guid.CreateVersion7();
        var orderId = Guid.CreateVersion7();
        var first = CommerceStreamFactFactory.OrderLifecycle(tenantId, orderId, "Confirmed", 12_34, "USD", DateTimeOffset.UtcNow);
        var duplicate = first with { };
        var second = CommerceStreamFactFactory.OrderLifecycle(tenantId, orderId, "Cancelled", 12_34, "USD", DateTimeOffset.UtcNow.AddMinutes(1));
        var records = new[]
        {
            ReplayRecord(0, first),
            ReplayRecord(1, duplicate),
            ReplayRecord(2, second),
        };
        var readModel = new OrderLifecycleReadModel();
        var runner = new StreamReplayRunner<OrderLifecycleStreamPayload>(
            new StreamEventConsumerProcessor<OrderLifecycleStreamPayload>(
                readModel,
                new InMemoryStreamProcessedEventStore(),
                new InMemoryStreamDeadLetterSink(),
                NullLogger<StreamEventConsumerProcessor<OrderLifecycleStreamPayload>>.Instance),
            new InMemoryStreamReplayWatermarkStore());

        var firstPass = await runner.ReplayAsync(StreamTopics.CommerceOrders, "orders-read-model", records);
        var secondPass = await runner.ReplayAsync(StreamTopics.CommerceOrders, "orders-read-model", records);

        Assert.Equal(new StreamReplayResult(2, 1, 0, 0, 2), firstPass);
        Assert.Equal(new StreamReplayResult(0, 0, 0, 3, 2), secondPass);
        Assert.Equal("Cancelled", readModel.StatusByOrderId[orderId]);
        Assert.Equal(2, readModel.AppliedCount);
    }

    [Fact]
    public void Stream_producer_di_uses_fake_when_event_streaming_is_disabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EventStreaming:Enabled"] = "false",
            })
            .Build();
        var services = new ServiceCollection();

        services.AddStreamProducer(configuration);

        using var provider = services.BuildServiceProvider();
        Assert.IsType<FakeStreamEventProducer>(provider.GetRequiredService<IStreamEventProducer>());
    }

    private sealed record TestStreamPayload(string Status);

    private sealed class RecordingStreamHandler : IStreamEventHandler<TestStreamPayload>
    {
        public int HandledCount { get; private set; }

        public Task HandleAsync(StreamConsumedEvent<TestStreamPayload> message, CancellationToken cancellationToken = default)
        {
            HandledCount++;
            return Task.CompletedTask;
        }
    }

    private static StreamReplayRecord ReplayRecord(long offset, StreamEventEnvelope<OrderLifecycleStreamPayload> envelope) => new(
        StreamTopics.CommerceOrders,
        envelope.PartitionKey,
        offset,
        JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        new Dictionary<string, string>());

    private sealed class OrderLifecycleReadModel : IStreamEventHandler<OrderLifecycleStreamPayload>
    {
        public Dictionary<Guid, string> StatusByOrderId { get; } = [];

        public int AppliedCount { get; private set; }

        public Task HandleAsync(StreamConsumedEvent<OrderLifecycleStreamPayload> message, CancellationToken cancellationToken = default)
        {
            StatusByOrderId[message.Envelope.Payload.OrderId] = message.Envelope.Payload.Status;
            AppliedCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingStreamProducer : IStreamEventProducer
    {
        public Task<StreamPublishResult> PublishAsync<TPayload>(string topic, StreamEventEnvelope<TPayload> envelope, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset now;

        public MutableTimeProvider(DateTimeOffset now)
        {
            this.now = now;
        }

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now = now.Add(by);
    }

    private sealed class NoopScheduledJob : IScheduledJob
    {
        public string Name => "noop";

        public string CronSchedule => "0 0/5 * * * ?";

        public Task ExecuteAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
