using System.Threading.RateLimiting;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ThreeCommerce.Gateway.Auth;

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
    });

// Tighter per-IP limits on auth endpoints (credential stuffing), permissive elsewhere.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var path = context.Request.Path.Value ?? string.Empty;
        var isAuthPath = path.StartsWith("/api/identity/login", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/identity/register", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/identity/password-reset", StringComparison.OrdinalIgnoreCase);

        return RateLimitPartition.GetFixedWindowLimiter(
            (isAuthPath ? "auth:" : "any:") + ip,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = isAuthPath ? 30 : 1000,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    });
});

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

app.UseRateLimiter();
app.UseMiddleware<SessionAuthMiddleware>();
app.MapReverseProxy();

app.Run();

public partial class Program;
