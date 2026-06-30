using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Observability;

public static class OtelExtensions
{
    /// <summary>
    /// Traces for HTTP in/out, EF, and MassTransit (message hops join the same trace), plus RED
    /// metrics (request rate/duration/errors) for HTTP in/out (mt6_13). Both export OTLP when
    /// OTEL_EXPORTER_OTLP_ENDPOINT is set; traces fall back to console otherwise. The metrics pipeline
    /// feeds the OTel Collector → Prometheus → Grafana stack (deploy/observability).
    /// </summary>
    public static TBuilder AddServiceTelemetry<TBuilder>(this TBuilder builder, string serviceName)
        where TBuilder : IHostApplicationBuilder
    {
        var useOtlp = !string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
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
                    .AddMeter(StreamMetrics.MeterName);

                // Metrics are ops data — only exported when a collector endpoint is configured (no
                // console spam by default). Prod creds are launch-gated; Grafana sits behind admin auth.
                if (useOtlp)
                {
                    metrics.AddOtlpExporter();
                }
            });

        return builder;
    }
}
