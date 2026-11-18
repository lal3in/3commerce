using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using OpenTelemetry.Trace;
using ThreeCommerce.BuildingBlocks.Infrastructure.Redis;
using ThreeCommerce.BuildingBlocks.Infrastructure.Streams;
using ThreeCommerce.BuildingBlocks.Observability;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Observability;

public static class OtelExtensions
{
    /// <summary>The MassTransit 8 activity source and meter (consume/publish/send counts, durations, faults).</summary>
    public const string MassTransitInstrumentation = "MassTransit";

    /// <summary>Npgsql's activity source (one span per database command).</summary>
    public const string NpgsqlActivitySource = "Npgsql";

    /// <summary>The StackExchange.Redis instrumentation's activity source.</summary>
    public const string RedisActivitySource = "OpenTelemetry.Instrumentation.StackExchangeRedis";

    /// <summary>Opt-in switch for the (beta) StackExchange.Redis span instrumentation.</summary>
    public const string RedisTracingSetting = "Telemetry:Redis:Tracing";

    /// <summary>
    /// The telemetry of every DB-owning service, the Notifications worker and the gateway — the light
    /// <see cref="TelemetryExtensions.AddTelemetryCore"/> (HTTP in/out traces with exceptions recorded, HTTP RED +
    /// <c>System.Runtime</c> metrics, OTLP logs, ADR-0064 release identity) plus:
    /// <list type="bullet">
    /// <item><b>traces</b>: MassTransit (message hops join the HTTP trace), Npgsql database commands (EF Core,
    /// raw SQL, the outbox — audit P1-1; parentless background-poll commands are dropped by
    /// <see cref="ChildSpansOnlyProcessor"/>), the Kafka stream lane's publish/process spans
    /// (<see cref="StreamTracing"/>, ADR-0034 amendment), and Valkey commands when
    /// <c>Telemetry:Redis:Tracing=true</c> (beta instrumentation, off by default);</item>
    /// <item><b>metrics</b>: the MassTransit meter (audit P1-5), Npgsql's pool/command meter, and the
    /// <c>3commerce.streams</c> / <c>3commerce.redis</c> meters.</item>
    /// </list>
    /// All three signals export OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set and fan out via the OTel Collector
    /// (deploy/observability): traces → Tempo, logs → Loki, metrics → Prometheus/Mimir, all fronted by Grafana.
    /// </summary>
    public static TBuilder AddServiceTelemetry<TBuilder>(this TBuilder builder, string serviceName)
        where TBuilder : IHostApplicationBuilder
    {
        var redisTracing = builder.Configuration.GetValue<bool>(RedisTracingSetting);

        builder.AddTelemetryCore(
            serviceName,
            tracing =>
            {
                tracing
                    .AddSource(MassTransitInstrumentation)
                    .AddNpgsql()
                    .AddSource(StreamTracing.ActivitySourceName);
                if (redisTracing)
                {
                    // Connections are attached when the shared multiplexer is first created (RedisExtensions).
                    tracing
                        .AddRedisInstrumentation()
                        .ConfigureRedisInstrumentation((services, instrumentation) =>
                            services.GetService<IRedisConnection>()?.WhenConnected(multiplexer => instrumentation.AddConnection(multiplexer)));
                }

                tracing.AddProcessor(new ChildSpansOnlyProcessor([NpgsqlActivitySource, RedisActivitySource]));
            },
            metrics => metrics
                .AddMeter(MassTransitInstrumentation)
                .AddNpgsqlInstrumentation()
                .AddMeter(StreamMetrics.MeterName)
                .AddMeter(RedisMetrics.MeterName));

        return builder;
    }
}
