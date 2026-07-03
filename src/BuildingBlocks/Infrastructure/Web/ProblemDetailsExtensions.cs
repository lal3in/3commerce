using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
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

            // Body-binding failures (malformed JSON, wrong enum shape — enums bind as NUMBERS on
            // this platform, see AGENTS.md) surface the framework's message: it names the offending
            // parameter/path and is safe + client-actionable.
            if (context.Exception is BadHttpRequestException bad && context.ProblemDetails.Status == StatusCodes.Status400BadRequest)
            {
                context.ProblemDetails.Detail ??= bad.Message;
            }
        });

        return services;
    }

    public static WebApplication UseApiProblemDetails(this WebApplication app)
    {
        // A malformed request body is the CLIENT's error: BadHttpRequestException (which wraps the
        // JsonException from minimal-API body binding) carries 400 — return it instead of a 500.
        // This turns the recurring enum-as-string bug class into clear 400s (finding F7 / rev_7).
        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            StatusCodeSelector = ex => ex is BadHttpRequestException bad
                ? bad.StatusCode
                : StatusCodes.Status500InternalServerError,
        });
        app.UseStatusCodePages();
        return app;
    }
}
