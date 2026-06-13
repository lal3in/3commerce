using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Web;

public static class ProblemDetailsExtensions
{
    /// <summary>RFC 9457 problem+json on every failure path, with traceId correlation.</summary>
    public static IServiceCollection AddApiProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            var traceId = Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier;
            context.ProblemDetails.Extensions.TryAdd("traceId", traceId);
        });

        return services;
    }

    public static WebApplication UseApiProblemDetails(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        return app;
    }
}
