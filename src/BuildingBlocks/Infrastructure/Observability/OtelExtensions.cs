using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ThreeCommerce.BuildingBlocks.Infrastructure.Redis;
using ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Observability;

public static class OtelExtensions
{
    /// <summary>
    /// Traces for HTTP in/out, EF, and MassTransit (message hops join the same trace), RED
    /// metrics (request rate/duration/errors) for HTTP in/out (mt6_13), and application logs.
    /// All three signals export OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set and fan out via the
    /// OTel Collector (deploy/observability): traces → Tempo, logs → Loki, metrics →
    /// Prometheus/Mimir — all fronted by Grafana. Without an endpoint, traces fall back to
    /// console; logs and metrics are OTLP-only (logs already reach the console through the
    /// default logging provider — the OTel logs pipeline is additive, never a replacement).
    /// Every signal shares one resource stamped with the <see cref="ReleaseIdentity"/> (ADR-0064):
    /// service.version, vcs.ref.head.revision, deployment.environment.name and one service.instance.id.
    /// </summary>
    public static TBuilder AddServiceTelemetry<TBuilder>(this TBuilder builder, string serviceName)
        where TBuilder : IHostApplicationBuilder
    {
        var useOtlp = !string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        var release = builder.AddReleaseIdentity(serviceName);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => release.Configure(resource))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddSource("MassTransit");

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
                    .AddMeter(StreamMetrics.MeterName)
                    .AddMeter(RedisMetrics.MeterName);

                // Metrics are ops data — only exported when a collector endpoint is configured (no
                // console spam by default). Prod creds are launch-gated; Grafana sits behind admin auth.
                if (useOtlp)
                {
                    metrics.AddOtlpExporter();
                }
            });

        // Logs are additive: the default console provider keeps emitting as-is; this pipeline
        // ships the same log records over OTLP (collector → Loki) with the SAME release resource
        // (same service.instance.id as traces/metrics) attached, so Grafana can correlate them with
        // traces/metrics by service_name and by release.
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

        return builder;
    }
}
