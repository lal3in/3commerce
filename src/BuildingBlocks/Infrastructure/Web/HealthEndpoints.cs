using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Web;

public static class HealthEndpoints
{
    /// <summary>Readiness includes the service's own database; MassTransit adds its bus check itself.</summary>
    public static IServiceCollection AddServiceHealth<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext
    {
        services.AddHealthChecks().AddDbContextCheck<TDbContext>("database");
        return services;
    }

    /// <summary>
    /// /health/live (process up) and /health/ready (db + bus). Internal only — the
    /// gateway must never route these publicly.
    /// </summary>
    public static IEndpointRouteBuilder MapServiceHealth(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready");
        return app;
    }
}
