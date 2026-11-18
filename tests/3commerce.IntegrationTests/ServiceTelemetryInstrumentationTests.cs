using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Instrumentation.Http;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using ThreeCommerce.BuildingBlocks.Infrastructure.Observability;
using ThreeCommerce.BuildingBlocks.Infrastructure.Streams;
using ThreeCommerce.BuildingBlocks.Observability;
using ThreeCommerce.BuildingBlocks.Observability.Blazor;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Unit lane (observability audit P0-3 / P1-1 / P1-4 / P1-5 / P1-6 / P2-4): what <c>AddServiceTelemetry</c> (services,
/// worker, gateway) and the light <c>AddBlazorServerTelemetry</c> (Admin, SupplierPortal) actually subscribe to —
/// exceptions recorded on HTTP spans, Npgsql DB spans kept inside a trace but dropped as background roots,
/// MassTransit / stream spans, and the MassTransit + System.Runtime meters.
/// </summary>
[Collection(StreamTracingCollection.Name)]
public sealed class ServiceTelemetryInstrumentationTests
{
    [Fact]
    public void Http_server_and_client_spans_record_exceptions()
    {
        using var host = Host(builder => builder.AddServiceTelemetry("telemetry-probe"));

        Assert.True(host.Services.GetRequiredService<IOptionsMonitor<AspNetCoreTraceInstrumentationOptions>>().Get(Options.DefaultName).RecordException);
        Assert.True(host.Services.GetRequiredService<IOptionsMonitor<HttpClientTraceInstrumentationOptions>>().Get(Options.DefaultName).RecordException);
    }

    [Fact]
    public void The_light_blazor_telemetry_records_exceptions_and_registers_the_circuit_handler_without_messaging_or_ef()
    {
        using var host = Host(builder => builder.AddBlazorServerTelemetry("blazor-probe"));

        Assert.True(host.Services.GetRequiredService<IOptionsMonitor<AspNetCoreTraceInstrumentationOptions>>().Get(Options.DefaultName).RecordException);
        Assert.Equal("blazor-probe", host.Services.GetRequiredService<ReleaseIdentity>().ServiceName);
        using var circuit = host.Services.CreateScope(); // circuit handlers are per-circuit (scoped)
        Assert.Contains(circuit.ServiceProvider.GetServices<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler>(), h => h is TelemetryCircuitHandler);
        // The light building block must not drag MassTransit or EF into the Blazor apps (audit P1-4).
        var references = typeof(TelemetryExtensions).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.DoesNotContain(references, name => name.StartsWith("MassTransit", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Npgsql", StringComparison.Ordinal));
    }

    [Fact]
    public void Db_spans_are_kept_inside_a_trace_and_background_poll_roots_are_dropped()
    {
        var captured = new CapturingProcessor();
        using var host = Host(builder => builder.AddServiceTelemetry("telemetry-probe"), captured);
        _ = host.Services.GetRequiredService<TracerProvider>();
        using var npgsql = new ActivitySource(OtelExtensions.NpgsqlActivitySource);
        using var request = new ActivitySource("telemetry-probe.request");
        using var requestListener = Listen("telemetry-probe.request");

        using (npgsql.StartActivity("outbox poll", ActivityKind.Client))
        {
            // a parentless background command — must not be exported
        }

        ActivityTraceId traceId;
        using (var parent = request.StartActivity("GET /orders", ActivityKind.Server)!)
        {
            traceId = parent.TraceId;
            using var db = npgsql.StartActivity("SELECT orders", ActivityKind.Client);
        }

        var dbSpans = captured.Exported.Where(a => a.Source.Name == OtelExtensions.NpgsqlActivitySource).ToList();
        var kept = Assert.Single(dbSpans);
        Assert.Equal("SELECT orders", kept.DisplayName);
        Assert.Equal(traceId, kept.TraceId);
    }

    [Fact]
    public void Messaging_and_stream_spans_are_subscribed()
    {
        var captured = new CapturingProcessor();
        using var host = Host(builder => builder.AddServiceTelemetry("telemetry-probe"), captured);
        _ = host.Services.GetRequiredService<TracerProvider>();
        using var massTransit = new ActivitySource(OtelExtensions.MassTransitInstrumentation);
        using var streams = new ActivitySource(StreamTracing.ActivitySourceName);

        using (massTransit.StartActivity("order-confirmed receive", ActivityKind.Consumer))
        {
        }

        using (streams.StartActivity("commerce.orders process", ActivityKind.Consumer))
        {
        }

        Assert.Contains(captured.Exported, a => a.Source.Name == OtelExtensions.MassTransitInstrumentation);
        Assert.Contains(captured.Exported, a => a.Source.Name == StreamTracing.ActivitySourceName);
    }

    [Fact]
    public void MassTransit_and_runtime_meters_are_exported()
    {
        var exported = new List<Metric>();
        using var host = Host(
            builder => builder.AddServiceTelemetry("telemetry-probe"),
            configureMetrics: metrics => metrics.AddInMemoryLikeReader(exported));
        var meterProvider = host.Services.GetRequiredService<MeterProvider>();
        using var massTransit = new Meter(OtelExtensions.MassTransitInstrumentation);
        massTransit.CreateCounter<long>("messaging.masstransit.consume").Add(1);

        meterProvider.ForceFlush();

        Assert.Contains(exported, m => m.MeterName == OtelExtensions.MassTransitInstrumentation && m.Name == "messaging.masstransit.consume");
        Assert.Contains(exported, m => m.MeterName == TelemetryNames.RuntimeMeter);
    }

    [Fact]
    public void Redis_span_instrumentation_is_off_unless_configured()
    {
        using var off = Host(builder => builder.AddServiceTelemetry("telemetry-probe"));
        using var on = Host(builder => builder.AddServiceTelemetry("telemetry-probe"), settings: new() { [OtelExtensions.RedisTracingSetting] = "true" });

        // Building the provider with the beta instrumentation enabled must work with no Redis configured at all.
        Assert.NotNull(on.Services.GetRequiredService<TracerProvider>());
        Assert.False(off.Services.GetRequiredService<IConfiguration>().GetValue<bool>(OtelExtensions.RedisTracingSetting));
    }

    private static ActivityListener Listen(string source)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static IHost Host(
        Action<HostApplicationBuilder> configure,
        CapturingProcessor? capture = null,
        Action<MeterProviderBuilder>? configureMetrics = null,
        Dictionary<string, string?>? settings = null)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // No exporter endpoint: traces would go to the console, so capture with our own processor instead.
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
        });
        if (settings is not null)
            builder.Configuration.AddInMemoryCollection(settings);
        configure(builder);
        if (capture is not null)
            builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddProcessor(capture));
        if (configureMetrics is not null)
            builder.Services.ConfigureOpenTelemetryMeterProvider(configureMetrics);
        return builder.Build();
    }

    /// <summary>Records what an exporter would receive: only activities still marked Recorded at the end.</summary>
    private sealed class CapturingProcessor : BaseProcessor<Activity>
    {
        private readonly List<Activity> exported = [];

        public IReadOnlyList<Activity> Exported
        {
            get
            {
                lock (exported)
                    return [.. exported];
            }
        }

        public override void OnEnd(Activity data)
        {
            if (!data.Recorded)
                return;
            lock (exported)
                exported.Add(data);
        }
    }
}

internal static class MetricCaptureExtensions
{
    /// <summary>A manual-flush reader whose exporter copies every batch into <paramref name="sink"/>.</summary>
    public static MeterProviderBuilder AddInMemoryLikeReader(this MeterProviderBuilder builder, List<Metric> sink) =>
        builder.AddReader(new BaseExportingMetricReader(new ListExporter(sink)) { TemporalityPreference = MetricReaderTemporalityPreference.Cumulative });

    private sealed class ListExporter(List<Metric> sink) : BaseExporter<Metric>
    {
        public override ExportResult Export(in Batch<Metric> batch)
        {
            lock (sink)
            {
                foreach (var metric in batch)
                    sink.Add(metric);
            }

            return ExportResult.Success;
        }
    }
}
