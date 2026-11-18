using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ThreeCommerce.BuildingBlocks.Contracts.Streams;
using ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Unit lane (ADR-0034 amendment 2026-11-15, owner decision Q6): the Kafka stream envelope carries the W3C trace
/// context of the operation that committed the fact, and a stream consumer's span joins that trace. Includes the
/// ADR-0055 back-compat pattern — an envelope written BEFORE the fields existed still parses, with null context.
/// </summary>
[Collection(StreamTracingCollection.Name)]
public sealed class StreamTracePropagationTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly ActivitySource Request = new("3commerce.tests.stream-request");
    private readonly ActivityListener listener;
    private readonly List<Activity> stopped = [];

    public StreamTracePropagationTests()
    {
        listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is StreamTracing.ActivitySourceName or "3commerce.tests.stream-request",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (stopped) stopped.Add(activity); },
        };
        ActivitySource.AddActivityListener(listener);
    }

    public void Dispose() => listener.Dispose();

    // Exactly the shape the stream outbox stored before the amendment: every field up to payload, nothing after.
    private const string PreAmendmentJson = """
        {
          "eventId": "01930000-0000-7000-8000-0000000000aa",
          "eventType": "OrderConfirmed",
          "eventVersion": 1,
          "schemaVersion": 1,
          "occurredAt": "2026-11-01T10:00:00+00:00",
          "sourceService": "ordering",
          "tenantId": "00000000-0000-0000-0000-000000000001",
          "aggregateId": "order-1",
          "partitionKey": "00000000-0000-0000-0000-000000000001:order-1",
          "correlationId": null,
          "causationId": null,
          "traceId": "0af7651916cd43dd8448eb211c80319c",
          "privacyClass": 2,
          "payload": { "status": "confirmed" }
        }
        """;

    [Fact]
    public void A_pre_amendment_envelope_still_parses_with_no_trace_context()
    {
        var envelope = JsonSerializer.Deserialize<StreamEventEnvelope<TestStreamPayload>>(PreAmendmentJson, Web)!;

        Assert.Equal("OrderConfirmed", envelope.EventType);
        Assert.Equal("confirmed", envelope.Payload.Status);
        Assert.Equal("0af7651916cd43dd8448eb211c80319c", envelope.TraceId);
        Assert.Null(envelope.TraceParent);
        Assert.Null(envelope.TraceState);
        Assert.Equal(StreamEventEnvelope<TestStreamPayload>.CurrentSchemaVersion, envelope.SchemaVersion); // additive: no bump
        Assert.False(StreamTracing.TryGetParentContext(envelope, out _));
    }

    [Fact]
    public async Task A_pre_amendment_envelope_is_processed_under_a_new_root_span()
    {
        var handler = new RecordingStreamHandler();
        var processor = Processor(handler);

        var result = await processor.ProcessAsync(StreamTopics.CommerceOrders, "k", 7, PreAmendmentJson, new Dictionary<string, string>(), "commerce.orders.dlq");

        Assert.Equal(StreamConsumerProcessResult.Processed, result);
        var process = Assert.Single(Stopped(a => a.OperationName == $"{StreamTopics.CommerceOrders} process" && a.GetTagItem("messaging.kafka.offset") is 7L));
        Assert.Equal(default, process.ParentSpanId);
        Assert.Equal(ActivityKind.Consumer, process.Kind);
    }

    [Fact]
    public void Create_carries_explicit_trace_context_and_it_round_trips()
    {
        const string traceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
        var envelope = Envelope() with { TraceParent = traceParent, TraceState = "vendor=1" };
        var created = StreamEventEnvelope<TestStreamPayload>.Create(
            envelope.EventId, envelope.EventType, 1, envelope.OccurredAt, "ordering", envelope.TenantId, "order-1",
            envelope.PartitionKey, StreamPrivacyClass.Internal, envelope.Payload, traceParent: $" {traceParent} ", traceState: "vendor=1");

        Assert.Equal(traceParent, created.TraceParent);
        var json = JsonSerializer.Serialize(created, Web);
        Assert.Contains("\"traceParent\":", json, StringComparison.Ordinal);
        var parsed = JsonSerializer.Deserialize<StreamEventEnvelope<TestStreamPayload>>(json, Web)!;
        Assert.Equal((traceParent, "vendor=1"), (parsed.TraceParent, parsed.TraceState));
        Assert.True(StreamTracing.TryGetParentContext(parsed, out var context));
        Assert.Equal("0af7651916cd43dd8448eb211c80319c", context.TraceId.ToHexString());
        Assert.Equal("b7ad6b7169203331", context.SpanId.ToHexString());
    }

    [Fact]
    public async Task Staging_captures_the_committing_operations_context_into_the_outbox_row()
    {
        var store = new InMemoryStreamOutboxStore();
        string expectedParent;
        using (var request = Request.StartActivity("POST /orders", ActivityKind.Server)!)
        {
            request.TraceStateString = "tenant=acme";
            expectedParent = request.Id!;
            await new StreamOutboxStager(store).StageAsync(StreamTopics.CommerceOrders, Envelope());
        }

        var row = Assert.Single(store.Messages);
        var staged = JsonSerializer.Deserialize<StreamEventEnvelope<TestStreamPayload>>(row.PayloadJson, Web)!;
        Assert.Equal(expectedParent, staged.TraceParent);
        Assert.Equal("tenant=acme", staged.TraceState);
        Assert.Equal(ActivityContext.Parse(expectedParent, null).TraceId.ToHexString(), staged.TraceId);
        var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(row.HeadersJson, Web)!;
        Assert.Equal(expectedParent, headers["traceparent"]);
        Assert.Equal("tenant=acme", headers["tracestate"]);
    }

    [Fact]
    public async Task Staging_outside_any_operation_writes_no_context()
    {
        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            var store = new InMemoryStreamOutboxStore();
            await new StreamOutboxStager(store).StageAsync(StreamTopics.CommerceOrders, Envelope());

            var staged = JsonSerializer.Deserialize<StreamEventEnvelope<TestStreamPayload>>(Assert.Single(store.Messages).PayloadJson, Web)!;
            Assert.Null(staged.TraceParent);
            Assert.DoesNotContain("traceparent", JsonSerializer.Deserialize<Dictionary<string, string>>(Assert.Single(store.Messages).HeadersJson, Web)!.Keys);
        }
        finally
        {
            Activity.Current = previous;
        }
    }

    [Fact]
    public void An_envelope_that_already_carries_a_context_is_not_overwritten()
    {
        const string original = "00-11111111111111111111111111111111-2222222222222222-01";
        using var request = Request.StartActivity("other", ActivityKind.Server)!;

        var stamped = StreamTracing.WithCurrentTraceContext(Envelope() with { TraceParent = original });

        Assert.Equal(original, stamped.TraceParent);
    }

    [Fact]
    public async Task Relay_publish_and_consume_join_the_committing_operations_trace()
    {
        var store = new InMemoryStreamOutboxStore();
        ActivityTraceId traceId;
        ActivitySpanId requestSpan;
        using (var request = Request.StartActivity("POST /orders", ActivityKind.Server)!)
        {
            (traceId, requestSpan) = (request.TraceId, request.SpanId);
            await new StreamOutboxStager(store).StageAsync(StreamTopics.CommerceOrders, Envelope());
        }

        // The relay runs later on its own loop — no ambient activity.
        var previous = Activity.Current;
        Activity.Current = null;
        var producer = new FakeStreamEventProducer();
        try
        {
            var relayed = await new StreamOutboxRelay(store, producer, TimeProvider.System, NullLogger<StreamOutboxRelay>.Instance).RelayOnceAsync();
            Assert.Equal(1, relayed.Published);
        }
        finally
        {
            Activity.Current = previous;
        }

        var published = Assert.Single(producer.Published);
        var handler = new RecordingStreamHandler();
        var result = await Processor(handler).ProcessAsync(published.Topic, published.Key, 1, published.EventJson, published.Headers, "commerce.orders.dlq");

        Assert.Equal(StreamConsumerProcessResult.Processed, result);
        var publish = Assert.Single(Stopped(a => a.TraceId == traceId && a.Kind == ActivityKind.Producer));
        var process = Assert.Single(Stopped(a => a.TraceId == traceId && a.Kind == ActivityKind.Consumer));
        Assert.Equal(requestSpan, publish.ParentSpanId);
        Assert.Equal(requestSpan, process.ParentSpanId);
        Assert.Contains(process.Links, link => link.Context.SpanId == requestSpan);
        Assert.Equal("kafka", process.GetTagItem("messaging.system"));
        Assert.Equal(StreamTopics.CommerceOrders, process.GetTagItem("messaging.destination.name"));
        // The handler ran INSIDE the consumer span, so everything it does (DB, HTTP, logs) is in the same trace.
        Assert.Equal(traceId, handler.TraceIds.Single());
    }

    [Fact]
    public async Task A_failing_handler_marks_the_consumer_span_failed()
    {
        using var request = Request.StartActivity("POST /orders", ActivityKind.Server)!;
        var envelope = StreamTracing.WithCurrentTraceContext(Envelope());
        var traceId = request.TraceId;
        request.Stop();

        var result = await Processor(new ThrowingStreamHandler()).ProcessAsync(
            StreamTopics.CommerceOrders, "k", 3, JsonSerializer.Serialize(envelope, Web), new Dictionary<string, string>(), "commerce.orders.dlq");

        Assert.Equal(StreamConsumerProcessResult.DeadLettered, result);
        var process = Assert.Single(Stopped(a => a.TraceId == traceId && a.Kind == ActivityKind.Consumer));
        Assert.Equal(ActivityStatusCode.Error, process.Status);
        Assert.Contains(process.Events, e => e.Name == "exception");
    }

    private List<Activity> Stopped(Func<Activity, bool> predicate)
    {
        lock (stopped)
            return stopped.Where(predicate).ToList();
    }

    private static StreamEventConsumerProcessor<TestStreamPayload> Processor(IStreamEventHandler<TestStreamPayload> handler) =>
        new(handler, new InMemoryStreamProcessedEventStore(), new InMemoryStreamDeadLetterSink(),
            NullLogger<StreamEventConsumerProcessor<TestStreamPayload>>.Instance);

    private static StreamEventEnvelope<TestStreamPayload> Envelope()
    {
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        return StreamEventEnvelope<TestStreamPayload>.Create(
            Guid.CreateVersion7(), "OrderConfirmed", 1, DateTimeOffset.UtcNow, "ordering", tenantId, "order-1",
            StreamPartitionKeys.TenantAggregate(tenantId, "order-1"), StreamPrivacyClass.Internal, new TestStreamPayload("confirmed"));
    }

    private sealed class RecordingStreamHandler : IStreamEventHandler<TestStreamPayload>
    {
        public List<ActivityTraceId> TraceIds { get; } = [];

        public Task HandleAsync(StreamConsumedEvent<TestStreamPayload> streamEvent, CancellationToken cancellationToken = default)
        {
            TraceIds.Add(Activity.Current?.TraceId ?? default);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingStreamHandler : IStreamEventHandler<TestStreamPayload>
    {
        public Task HandleAsync(StreamConsumedEvent<TestStreamPayload> streamEvent, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("projection failed");
    }
}

/// <summary>Stream-tracing tests share the static <c>3commerce.streams</c> ActivitySource, so they never run in parallel.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StreamTracingCollection
{
    public const string Name = "stream-tracing";
}
