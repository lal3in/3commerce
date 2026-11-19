using Microsoft.AspNetCore.Http;
using Sentry.Extensibility;
using ThreeCommerce.BuildingBlocks.Contracts.Abstractions;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Observability;

/// <summary>
/// Keeps CLIENT errors out of the GlitchTip inbox (ADR-0067). The shared exception handler
/// (<c>UseApiProblemDetails</c>) answers a typed domain failure (<see cref="IProblemException"/>) or a malformed body
/// (<see cref="BadHttpRequestException"/>) with a 4xx, but ASP.NET Core still logs every exception it handles at
/// <c>Error</c>. Those are the caller's mistakes, not defects: an inbox full of "validation failed" issues would bury
/// the real ones. A 5xx-mapped exception of the same types still goes through.
/// </summary>
public sealed class ClientErrorExceptionFilter : IExceptionFilter
{
    /// <summary>True = drop the event.</summary>
    public bool Filter(Exception ex) => ex switch
    {
        IProblemException problem => problem.StatusCode < StatusCodes.Status500InternalServerError,
        BadHttpRequestException bad => bad.StatusCode < StatusCodes.Status500InternalServerError,
        _ => false,
    };
}
