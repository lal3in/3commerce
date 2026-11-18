using System.Diagnostics;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;

namespace ThreeCommerce.BuildingBlocks.Observability.Blazor;

/// <summary>
/// An <see cref="ErrorBoundary"/> that LOGS what it catches (audit P1-4): the exception goes to
/// <see cref="ILogger"/> at Error — and so over OTLP to Loki with the trace id and the circuit's tenant/user scope
/// (<see cref="TelemetryCircuitHandler"/>) — instead of only the browser console. The UI shows a short notice
/// with a reference (the trace id when there is one) the operator can quote; it never shows the exception text.
/// The circuit survives: the rest of the page keeps working and navigating away recovers the boundary.
/// </summary>
public sealed partial class LoggingErrorBoundary : ErrorBoundary
{
    [Inject] private ILogger<LoggingErrorBoundary> Logger { get; set; } = default!;

    [Inject] private NavigationManager Navigation { get; set; } = default!;

    /// <summary>The reference shown for the current error (trace id, or a short random id without a trace).</summary>
    public string? Reference { get; private set; }

    protected override Task OnErrorAsync(Exception exception)
    {
        Reference = ReferenceFor(Activity.Current);
        // The page path only — never the query string (it can carry emails/search terms).
        var page = new Uri(Navigation.Uri).AbsolutePath;
        LogCaught(Logger, exception, page, Reference);
        return Task.CompletedTask;
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (CurrentException is null)
        {
            builder.AddContent(0, ChildContent);
            return;
        }

        if (ErrorContent is not null)
        {
            builder.AddContent(1, ErrorContent(CurrentException));
            return;
        }

        builder.OpenElement(2, "div");
        builder.AddAttribute(3, "class", "blazor-error-boundary");
        builder.AddAttribute(4, "role", "alert");
        builder.AddAttribute(5, "data-error-reference", Reference);
        builder.AddAttribute(6, "style", "padding:1rem; border:1px solid #fca5a5; background:#fef2f2; color:#991b1b; border-radius:6px;");
        builder.AddContent(7, $"Something went wrong on this page. Reference: {Reference}");
        builder.CloseElement();
    }

    public static string ReferenceFor(Activity? activity) =>
        activity is { TraceId: var traceId } && traceId != default
            ? traceId.ToHexString()
            : Guid.CreateVersion7().ToString("N")[^12..];

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Unhandled UI error caught by the error boundary on {Page} (reference {ErrorReference})")]
    private static partial void LogCaught(ILogger logger, Exception exception, string page, string errorReference);
}
