using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Web;

public static class HealthEndpoints
{
    /// <summary>The internal error probe (see <see cref="MapServiceHealth"/>).</summary>
    public const string ErrorProbePath = "/health/error-probe";

    /// <summary>Readiness includes the service's own database; MassTransit adds its bus check itself.</summary>
    public static IServiceCollection AddServiceHealth<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext
    {
        services.AddHealthChecks().AddDbContextCheck<TDbContext>("database");
        return services;
    }

    /// <summary>
    /// /health/live (process up) and /health/ready (db + bus). Internal only — the
    /// gateway must never route these publicly (it answers 404 for any /api/*/health* path).
    /// Outside Production also <see cref="ErrorProbePath"/>: an unhandled exception on purpose, so the live suite
    /// (e2e-verify L20c) can prove the whole error path — exception handler → Error log → GlitchTip issue carrying
    /// the release and the trace id the 500 problem+json returns (ADR-0067). Not mapped in Production at all.
    /// </summary>
    public static IEndpointRouteBuilder MapServiceHealth(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready");
        if (!app.ServiceProvider.GetRequiredService<IHostEnvironment>().IsProduction())
        {
            app.MapGet(ErrorProbePath, IResult () => throw new ErrorProbeException()).ExcludeFromDescription();
        }

        return app;
    }
}

/// <summary>Thrown only by the internal error probe; its type names the issue it creates in the error inbox.</summary>
public sealed class ErrorProbeException()
    : InvalidOperationException("Error probe: a deliberate unhandled exception (error-inbox verification).");
