using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ThreeCommerce.BuildingBlocks.Infrastructure.Redis;
using ThreeCommerce.Gateway.Auth;
using ThreeCommerce.Gateway.RateLimiting;
using ThreeCommerce.Gateway.Tenancy;

var builder = WebApplication.CreateBuilder(args);
// Containerized launch: load host wiring without coupling to ASPNETCORE_ENVIRONMENT (see ContainerConfig).
if (string.Equals(Environment.GetEnvironmentVariable("USE_CONTAINER_CONFIG"), "true", StringComparison.OrdinalIgnoreCase))
    builder.Configuration.AddJsonFile("appsettings.Container.json", optional: true, reloadOnChange: false);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("gateway"))
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
        if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
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
        // Export the gateway's Redis fast-path metrics (rate-limit decisions, fallbacks) so the
        // redis-overview Grafana dashboard sees the gateway too (ADR-0044). OTLP-only, like the services.
        metrics.AddAspNetCoreInstrumentation().AddMeter(RedisMetrics.MeterName);
        if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            metrics.AddOtlpExporter();
        }
    });

// Gateway rate limiting (ADR-0044). Backend=Redis makes limits correct across replicas (the legacy
// in-process limiter gave each replica its own window). Auth endpoints get a tight per-IP window against
// credential stuffing; everything else is permissive. Partition + limits live in RateLimitPolicy so both
// backends agree. The Redis client degrades to a no-op when unconfigured, and the OnRedisOutage toggle
// decides fail-open vs fail-closed — see appsettings.json "RateLimiting".
builder.AddRedis();
builder.Services.Configure<RateLimitOptions>(builder.Configuration.GetSection("RateLimiting"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<RateLimitOptions>>().Value);
builder.Services.AddSingleton<IRateLimitStore, RedisRateLimitStore>();
builder.Services.AddSingleton(sp => RateLimitPolicy.CreateInProcessLimiter(sp.GetRequiredService<RateLimitOptions>()));

builder.Services.Configure<StorefrontDomainOptions>(builder.Configuration.GetSection("Tenancy"));
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient("identity", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Identity:BaseUrl"] ?? "http://localhost:5101");
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddSingleton<InternalClaimsMinter>();

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;

    // Gateway's own liveness probe (container healthcheck) — early + terminal, before auth.
    if (string.Equals(path, "/health", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.WriteAsync("ok");
        return;
    }

    // Service health endpoints are internal-only: never proxy /api/{service}/health/*.
    if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
        && path.Contains("/health", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await next();
});

app.UseMiddleware<DomainResolutionMiddleware>();
app.UseMiddleware<DistributedRateLimitMiddleware>();
app.UseMiddleware<SessionAuthMiddleware>();
app.MapReverseProxy();

app.Run();

public partial class Program;
