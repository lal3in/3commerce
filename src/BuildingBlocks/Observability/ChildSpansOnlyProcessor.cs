using System.Diagnostics;
using OpenTelemetry;

namespace ThreeCommerce.BuildingBlocks.Observability;

/// <summary>
/// Drops ROOT spans (no parent) from the named client-library sources, keeping their spans only when they belong
/// to a real operation's trace. Background pollers — the MassTransit EF outbox/inbox delivery loop, the stream
/// outbox relay, Quartz, health checks — issue a database or cache command every second in every service; as
/// parentless root spans they would flood Tempo and the span-metrics with one-span "traces" that say nothing.
/// <para>A dropped span has <see cref="Activity.Recorded"/> cleared in <see cref="OnStart"/>; the OTLP/console
/// export processors skip unrecorded activities, and so do the Tempo span-metrics derived from them.</para>
/// </summary>
public sealed class ChildSpansOnlyProcessor(IEnumerable<string> sourceNames) : BaseProcessor<Activity>
{
    private readonly HashSet<string> sources = new(sourceNames, StringComparer.Ordinal);

    public override void OnStart(Activity data)
    {
        if (data.Parent is null && data.ParentSpanId == default && sources.Contains(data.Source.Name))
        {
            data.IsAllDataRequested = false;
            data.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        }
    }
}
