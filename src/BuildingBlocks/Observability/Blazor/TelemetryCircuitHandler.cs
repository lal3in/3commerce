using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Logging;

namespace ThreeCommerce.BuildingBlocks.Observability.Blazor;

/// <summary>
/// Logs the Blazor Server circuit lifecycle — opened, connection down/up, closed (with its duration) — with the
/// circuit id and the signed-in principal's <b>tenant and user ids</b> (never email/name: no PII), and wraps every
/// inbound circuit activity (UI event, render, JS interop) in a logging scope carrying the same ids. With the OTel
/// logs pipeline's <c>IncludeScopes</c>, every log written while handling a circuit event — including the
/// framework's own "Unhandled exception in circuit" — reaches Loki tagged with <c>circuit_id</c>/<c>tenant_id</c>/
/// <c>user_id</c> (audit P1-4). Anonymous circuits (the login page) log <c>anonymous</c>.
/// </summary>
public sealed partial class TelemetryCircuitHandler(
    ILogger<TelemetryCircuitHandler> logger,
    AuthenticationStateProvider? authenticationStateProvider = null) : CircuitHandler
{
    public const string Anonymous = "anonymous";

    private string circuitId = "unknown";
    private long openedAt;
    private CircuitPrincipal? principal;

    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        circuitId = circuit.Id;
        openedAt = Stopwatch.GetTimestamp();
        var who = await ResolvePrincipalAsync();
        LogOpened(logger, circuitId, who.TenantId, who.UserId);
    }

    public override async Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var who = await ResolvePrincipalAsync();
        LogConnectionDown(logger, circuit.Id, who.TenantId, who.UserId);
    }

    public override async Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var who = await ResolvePrincipalAsync();
        LogConnectionUp(logger, circuit.Id, who.TenantId, who.UserId);
    }

    public override async Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var who = await ResolvePrincipalAsync();
        var seconds = openedAt == 0 ? 0 : Stopwatch.GetElapsedTime(openedAt).TotalSeconds;
        LogClosed(logger, circuit.Id, who.TenantId, who.UserId, Math.Round(seconds, 1));
    }

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next) =>
        async context =>
        {
            var who = await ResolvePrincipalAsync();
            Activity.Current?.SetTag("enduser.id", who.UserId).SetTag("tenant.id", who.TenantId);
            using (logger.BeginScope(new Dictionary<string, object>
            {
                ["circuit_id"] = context.Circuit.Id,
                ["tenant_id"] = who.TenantId,
                ["user_id"] = who.UserId,
            }))
            {
                await next(context);
            }
        };

    /// <summary>Reads the opaque tenant/user ids from the principal; anything missing reads "anonymous".</summary>
    public static CircuitPrincipal Describe(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
            return new CircuitPrincipal(Anonymous, Anonymous);

        return new CircuitPrincipal(
            OpaqueId(user.FindFirst(TelemetryClaims.TenantId)?.Value),
            OpaqueId(user.FindFirst(TelemetryClaims.UserId)?.Value));
    }

    // Only a GUID is ever logged — a claim that is not one (misconfigured, or someone put an email there) is
    // reported as "unknown" rather than leaked.
    private static string OpaqueId(string? value) =>
        Guid.TryParse(value, out var id) ? id.ToString() : "unknown";

    private async Task<CircuitPrincipal> ResolvePrincipalAsync()
    {
        if (principal is { UserId: not Anonymous } known)
            return known;

        try
        {
            var state = authenticationStateProvider is null ? null : await authenticationStateProvider.GetAuthenticationStateAsync();
            principal = Describe(state?.User);
        }
        catch (InvalidOperationException)
        {
            // The auth state is not available yet at this point of the circuit's life — report anonymous for now.
            principal = new CircuitPrincipal(Anonymous, Anonymous);
        }

        return principal;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Blazor circuit {CircuitId} opened (tenant {TenantId}, user {UserId})")]
    private static partial void LogOpened(ILogger logger, string circuitId, string tenantId, string userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Blazor circuit {CircuitId} connection down (tenant {TenantId}, user {UserId})")]
    private static partial void LogConnectionDown(ILogger logger, string circuitId, string tenantId, string userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Blazor circuit {CircuitId} connection up (tenant {TenantId}, user {UserId})")]
    private static partial void LogConnectionUp(ILogger logger, string circuitId, string tenantId, string userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Blazor circuit {CircuitId} closed after {DurationSeconds}s (tenant {TenantId}, user {UserId})")]
    private static partial void LogClosed(ILogger logger, string circuitId, string tenantId, string userId, double durationSeconds);
}

/// <summary>The opaque ids a circuit's telemetry carries (GUIDs, <c>anonymous</c> or <c>unknown</c>).</summary>
public sealed record CircuitPrincipal(string TenantId, string UserId);
