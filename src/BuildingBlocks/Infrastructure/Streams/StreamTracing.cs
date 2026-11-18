using System.Diagnostics;
using ThreeCommerce.BuildingBlocks.Contracts.Streams;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

/// <summary>
/// W3C trace propagation over the Kafka stream lane (ADR-0034 amendment, owner decision Q6). Confluent.Kafka has
/// no OpenTelemetry instrumentation, so the lane carries the context itself:
/// <list type="number">
/// <item><b>Stage</b> (inside the request/consumer that commits the fact): the current activity's
/// <c>traceparent</c>/<c>tracestate</c> are written into the envelope (<see cref="WithCurrentTraceContext{TPayload}"/>),
/// so the context survives the stream outbox row and the relay's later, unrelated background loop.</item>
/// <item><b>Publish</b>: a Producer span <c>"{topic} publish"</c>, a child of the envelope's context.</item>
/// <item><b>Process</b>: a Consumer span <c>"{topic} process"</c>, a child of the envelope's context — the stream
/// consumer joins the producer's trace — with a span link to the same context.</item>
/// </list>
/// An envelope without the fields (written before this change) simply starts a new root span.
/// </summary>
public static class StreamTracing
{
    public const string ActivitySourceName = "3commerce.streams";

    internal static readonly ActivitySource Source = new(ActivitySourceName);

    /// <summary>
    /// Returns the envelope stamped with the CURRENT activity's W3C context (<c>traceparent</c>, <c>tracestate</c>
    /// and, when missing, <c>traceId</c>). An envelope that already carries a <c>traceparent</c>, or a call with no
    /// current W3C activity, is returned unchanged.
    /// </summary>
    public static StreamEventEnvelope<TPayload> WithCurrentTraceContext<TPayload>(StreamEventEnvelope<TPayload> envelope)
    {
        var current = Activity.Current;
        if (!string.IsNullOrWhiteSpace(envelope.TraceParent) || current is null || current.IdFormat != ActivityIdFormat.W3C)
            return envelope;

        return envelope with
        {
            TraceParent = current.Id,
            TraceState = string.IsNullOrEmpty(current.TraceStateString) ? null : current.TraceStateString,
            TraceId = envelope.TraceId ?? current.TraceId.ToHexString(),
        };
    }

    /// <summary>Parses the envelope's <c>traceparent</c>/<c>tracestate</c>; false when absent or malformed.</summary>
    public static bool TryGetParentContext<TPayload>(StreamEventEnvelope<TPayload> envelope, out ActivityContext context)
    {
        context = default;
        return !string.IsNullOrWhiteSpace(envelope.TraceParent)
            && ActivityContext.TryParse(envelope.TraceParent, envelope.TraceState, isRemote: true, out context);
    }

    /// <summary>The Producer span for one publish to <paramref name="topic"/> (null when nobody listens).</summary>
    public static Activity? StartPublish<TPayload>(string topic, StreamEventEnvelope<TPayload> envelope)
    {
        var parent = TryGetParentContext(envelope, out var context) ? context : default;
        var activity = parent == default
            ? Source.StartActivity($"{topic} publish", ActivityKind.Producer)
            : Source.StartActivity($"{topic} publish", ActivityKind.Producer, parent);
        return Tag(activity, "publish", topic, envelope);
    }

    /// <summary>
    /// The Consumer span for processing one record from <paramref name="topic"/>: a child of the producer's context
    /// (so it joins that trace) plus a link to it; a new root when the envelope carries no context.
    /// </summary>
    public static Activity? StartProcess<TPayload>(string topic, long offset, StreamEventEnvelope<TPayload> envelope)
    {
        Activity? activity;
        if (TryGetParentContext(envelope, out var context))
        {
            activity = Source.StartActivity(
                $"{topic} process", ActivityKind.Consumer, context, links: [new ActivityLink(context)]);
        }
        else
        {
            activity = Source.StartActivity($"{topic} process", ActivityKind.Consumer);
        }

        activity?.SetTag("messaging.kafka.offset", offset);
        return Tag(activity, "process", topic, envelope);
    }

    private static Activity? Tag<TPayload>(Activity? activity, string operation, string topic, StreamEventEnvelope<TPayload> envelope)
    {
        if (activity is null)
            return null;

        // OTel messaging semantic conventions; ids only — never the payload (ADR-0034 privacy classes).
        activity.SetTag("messaging.system", "kafka");
        activity.SetTag("messaging.operation.type", operation);
        activity.SetTag("messaging.destination.name", topic);
        activity.SetTag("messaging.message.id", envelope.EventId.ToString());
        activity.SetTag("messaging.kafka.message.key", envelope.PartitionKey);
        activity.SetTag("stream.event_type", envelope.EventType);
        activity.SetTag("stream.event_version", envelope.EventVersion);
        if (envelope.TenantId is { } tenantId)
            activity.SetTag("tenant.id", tenantId.ToString());
        return activity;
    }

    /// <summary>Marks the span failed and records the exception as a span event.</summary>
    public static void RecordFailure(Activity? activity, Exception exception)
    {
        if (activity is null)
            return;

        activity.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
        activity.AddException(exception);
    }
}
