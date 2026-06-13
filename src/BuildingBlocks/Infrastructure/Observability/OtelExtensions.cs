using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Observability;

public static class OtelExtensions
{
    /// <summary>
    /// Traces for HTTP in/out, EF, and MassTransit (message hops join the same trace).
    /// Exports OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set, console otherwise.
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
            });

        return builder;
    }
}
