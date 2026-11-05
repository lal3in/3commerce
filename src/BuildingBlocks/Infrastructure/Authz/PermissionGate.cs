using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Authz;

/// <summary>The PEP's verdict for one action permission.</summary>
public enum PermissionVerdict
{
    Allowed = 0,

    /// <summary>The PDP said no, or the caller carries no resolvable identity.</summary>
    Denied = 1,

    /// <summary>The PDP could not be reached — fail closed (ADR-0025: high-risk decisions deny).</summary>
    Unavailable = 2,
}

/// <summary>
/// Service-side Policy Enforcement Point for one action permission (ADR-0025; refund_permission_enforcement).
/// The route group's role policy (<c>Admin</c>) only says "this is a back-office caller"; the gate asks the
/// Identity PDP whether that caller's tenant roles grant the permission, and refuses otherwise:
/// <list type="bullet">
/// <item>denied → <c>403</c> problem+json (<c>errorCode: permission_denied</c>, <c>permission</c>) and a
/// <see cref="AuditOutcome.Denied"/> audit entry naming the action, flushed through the caller's outbox;</item>
/// <item>PDP unreachable → <c>503</c> problem+json (<c>errorCode: authorization_unavailable</c>) — never allowed.</item>
/// </list>
/// The decision is never cached: these are high-risk money actions.
/// </summary>
public sealed class PermissionGate(PolicyDecisionClient pdp, IAuditRecorder audit, IConfiguration config, ILogger<PermissionGate> logger)
{
    public const string DeniedErrorCode = "permission_denied";
    public const string UnavailableErrorCode = "authorization_unavailable";

    /// <summary>Asks the PDP for one action permission on behalf of the internal-claims caller.</summary>
    public async Task<PermissionVerdict> CheckAsync(ClaimsPrincipal user, string permissionKey, CancellationToken ct)
    {
        if (!Guid.TryParse(user.FindFirstValue("sub"), out var userId))
        {
            return PermissionVerdict.Denied;
        }

        Guid? tenantId = Guid.TryParse(user.FindFirstValue("tenant"), out var tenant) ? tenant : null;
        PolicyDecisionResponse? response;
        try
        {
            response = await pdp.DecideAsync(
                new PolicyDecisionRequest(Guid.Empty, tenantId, [permissionKey], [], userId), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "PDP unavailable deciding {Permission} for user {UserId}; failing closed", permissionKey, userId);
            return PermissionVerdict.Unavailable;
        }

        // 404 from the PDP = no active principal behind this user in this tenant: nothing is granted.
        var decision = response?.Actions.FirstOrDefault(a => string.Equals(a.PermissionKey, permissionKey, StringComparison.Ordinal));
        return decision is { Allowed: true } ? PermissionVerdict.Allowed : PermissionVerdict.Denied;
    }

    /// <summary>
    /// Null when <paramref name="user"/> holds <paramref name="permissionKey"/>; otherwise the problem result to
    /// return. A denial is audited as <see cref="AuditOutcome.Denied"/> on (<paramref name="resourceType"/>,
    /// <paramref name="resourceId"/>, <paramref name="action"/>) and committed via <paramref name="db"/>'s outbox.
    /// </summary>
    public async Task<ProblemHttpResult?> DenyUnlessAllowedAsync(
        ClaimsPrincipal user, string permissionKey, string resourceType, string resourceId, string action,
        DbContext db, CancellationToken ct)
    {
        var verdict = await CheckAsync(user, permissionKey, ct);
        switch (verdict)
        {
            case PermissionVerdict.Allowed:
                return null;
            case PermissionVerdict.Unavailable:
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Authorization unavailable",
                    detail: "The permission check could not be completed; nothing was changed. Try again shortly.",
                    extensions: new Dictionary<string, object?> { ["errorCode"] = UnavailableErrorCode, ["permission"] = permissionKey });
            default:
                await audit.RecordAsync(user.DeniedAttempt(
                    TenantFor(user), resourceType, resourceId, action, $"missing permission {permissionKey}"), ct);
                await db.SaveChangesAsync(ct); // flush the audit publish through the bus outbox
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Permission required",
                    detail: $"This action requires the '{permissionKey}' permission.",
                    extensions: new Dictionary<string, object?> { ["errorCode"] = DeniedErrorCode, ["permission"] = permissionKey });
        }
    }

    // The caller's tenant (gateway-minted claim); else the configured default tenant the refund audit uses.
    private Guid TenantFor(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue("tenant"), out var tenant) ? tenant
        : Guid.TryParse(config["Tenancy:DefaultTenantId"], out var configured) ? configured
        : new Guid("00000000-0000-0000-0000-000000000001");
}

public static class PermissionGateRegistration
{
    /// <summary>
    /// Registers the PDP client (Identity base URL from <c>Authz:IdentityBaseUrl</c>, default the bare-run
    /// Identity on :5101) and the <see cref="PermissionGate"/>. The caller must also register an
    /// <see cref="IAuditRecorder"/> (AddAuditRecorder).
    /// </summary>
    public static IServiceCollection AddPermissionGate(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPolicyDecisionClient(configuration["Authz:IdentityBaseUrl"] ?? "http://localhost:5101");
        services.AddScoped<PermissionGate>();
        return services;
    }
}
