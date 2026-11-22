using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Sentry.Extensions.Hosting;

namespace ThreeCommerce.BuildingBlocks.Observability;

/// <summary>Well-known instrument names (meters / activity sources) the telemetry pipelines subscribe to.</summary>
public static class TelemetryNames
{
    /// <summary>The .NET 9+ built-in runtime meter: GC, heap, thread pool, exceptions, JIT, locks (audit P1-6).</summary>
    public const string RuntimeMeter = "System.Runtime";

    /// <summary>.NET 10 Blazor activity source + meters (component/circuit lifecycle) — Admin and SupplierPortal.</summary>
    public const string ComponentsActivitySource = "Microsoft.AspNetCore.Components";
    public const string ComponentsMeter = "Microsoft.AspNetCore.Components";
    public const string ComponentsLifecycleMeter = "Microsoft.AspNetCore.Components.Lifecycle";
    public const string CircuitsMeter = "Microsoft.AspNetCore.Components.Server.Circuits";
}

public static class TelemetryExtensions
{
    /// <summary>
    /// The light OpenTelemetry setup every .NET host shares (audit P0-3/P1-4/P1-6): one resource stamped with the
    /// <see cref="ReleaseIdentity"/> (ADR-0064) for all three signals;
    /// <b>traces</b> for HTTP in (ASP.NET Core) and out (HttpClient), both recording exceptions as span events
    /// (<c>exception.type/message/stacktrace</c>, so a red span in Tempo carries its stack);
    /// <b>metrics</b> for HTTP in/out plus the built-in <c>System.Runtime</c> meter;
    /// <b>logs</b> over OTLP. Everything exports OTLP when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set; without it
    /// traces fall back to the console and metrics/logs are not exported (logs still reach the console through the
    /// default provider — the OTel logs pipeline is additive). <paramref name="configureTracing"/> and
    /// <paramref name="configureMetrics"/> add a host's own sources/meters BEFORE the exporter is attached.
    /// <b>Errors</b> (Error/Critical log records) also go to the GlitchTip inbox when <c>Sentry:Dsn</c>/<c>SENTRY_DSN</c>
    /// is set (<see cref="ErrorTracking"/>, ADR-0067); <paramref name="configureErrorTracking"/> adds a host's filters.
    /// No MassTransit, EF or Redis here — that is <c>AddServiceTelemetry</c> (BuildingBlocks.Infrastructure).
    /// </summary>
    public static ReleaseIdentity AddTelemetryCore<TBuilder>(
        this TBuilder builder,
        string serviceName,
        Action<TracerProviderBuilder>? configureTracing = null,
        Action<MeterProviderBuilder>? configureMetrics = null,
        Action<SentryGenericHostOptions>? configureErrorTracking = null)
        where TBuilder : IHostApplicationBuilder
    {
        var useOtlp = UsesOtlp(builder.Configuration);
        var release = builder.AddReleaseIdentity(serviceName);

        // Errors also go to the GlitchTip inbox when a DSN is configured (ADR-0067); off otherwise.
        builder.AddErrorTracking(release, configureErrorTracking);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => release.Configure(resource))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation(options => options.RecordException = true)
                    .AddHttpClientInstrumentation(options => options.RecordException = true);
                configureTracing?.Invoke(tracing);

                if (useOtlp)
                {
                    tracing.AddOtlpExporter();
                }
                else
                {
                    tracing.AddConsoleExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddMeter(TelemetryNames.RuntimeMeter);
                configureMetrics?.Invoke(metrics);

                // Metrics are ops data — only exported when a collector endpoint is configured (no console spam).
                if (useOtlp)
                {
                    metrics.AddOtlpExporter();
                }
            });

        // Logs are additive: the console provider keeps emitting as-is; this pipeline ships the same records over
        // OTLP (collector → Loki) with the SAME release resource (same service.instance.id as traces/metrics).
        // Scopes are included, so a logging scope (e.g. the Blazor circuit's tenant/user ids) becomes attributes.
        if (useOtlp)
        {
            builder.Logging.AddOpenTelemetry(logging =>
            {
                logging.SetResourceBuilder(release.Configure(ResourceBuilder.CreateDefault()));
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = true;
                logging.ParseStateValues = true;
                logging.AddOtlpExporter();
            });
        }

        return release;
    }

    /// <summary>True when an OTLP endpoint is configured (<c>OTEL_EXPORTER_OTLP_ENDPOINT</c>).</summary>
    public static bool UsesOtlp(IConfiguration configuration) =>
        !string.IsNullOrEmpty(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
}
