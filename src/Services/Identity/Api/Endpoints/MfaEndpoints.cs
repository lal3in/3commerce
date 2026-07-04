using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Identity.Domain;
using ThreeCommerce.Identity.Infrastructure;

namespace ThreeCommerce.Identity.Api.Endpoints;

/// <summary>
/// TOTP second factor (mt6_10 enforcement half). All routes are keyed by the raw session cookie —
/// like Logout — because an MFA-pending session introspects to nothing and therefore carries no
/// claims anywhere else on the platform.
/// </summary>
public static class MfaEndpoints
{
    public static IEndpointRouteBuilder MapMfa(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/mfa").WithTags("Mfa");

        group.MapGet("/status", Status);
        group.MapPost("/enroll/begin", BeginEnrollment);
        group.MapPost("/enroll/confirm", ConfirmEnrollment);
        group.MapPost("/challenge", Challenge);
        group.MapPost("/step-up", StepUp);

        // Tenant policy (admin, own tenant): the platform minimum is a floor — Effective = max(both).
        group.MapGet("/policy", GetPolicy).RequireAuthorization(InternalClaimsAuth.AdminPolicy);
        group.MapPut("/policy", SetPolicy).RequireAuthorization(InternalClaimsAuth.AdminPolicy);

        return app;
    }

    private static async Task<Results<Ok<MfaPolicyResponse>, ForbidHttpResult>> GetPolicy(
        ClaimsPrincipal principal, IdentityDbContext db, MfaPlatformPolicy platform, CancellationToken ct)
    {
        if (!Guid.TryParse(principal.FindFirstValue("tenant"), out var tenantId))
        {
            return TypedResults.Forbid();
        }

        var tenantPolicy = await db.Tenants.Where(t => t.Id == tenantId).Select(t => t.MfaPolicy).SingleAsync(ct);
        return TypedResults.Ok(new MfaPolicyResponse(
            platform.Minimum, tenantPolicy, new MfaPolicy(platform.Minimum, tenantPolicy).Effective));
    }

    private static async Task<Results<Ok<MfaPolicyResponse>, BadRequest<MessageResponse>, ForbidHttpResult>> SetPolicy(
        SetMfaPolicyRequest request, ClaimsPrincipal principal, IdentityDbContext db, MfaPlatformPolicy platform, CancellationToken ct)
    {
        if (!Guid.TryParse(principal.FindFirstValue("tenant"), out var tenantId))
        {
            return TypedResults.Forbid();
        }

        if (!Enum.IsDefined(request.Policy))
        {
            return TypedResults.BadRequest(new MessageResponse("Unknown MFA requirement value."));
        }

        var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId, ct);
        tenant.MfaPolicy = request.Policy;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new MfaPolicyResponse(
            platform.Minimum, tenant.MfaPolicy, new MfaPolicy(platform.Minimum, tenant.MfaPolicy).Effective));
    }

    private static async Task<Results<Ok<MfaStatus>, UnauthorizedHttpResult>> Status(
        HttpContext http, IAuthService auth, CancellationToken ct)
    {
        var status = SessionToken(http) is { } token ? await auth.GetMfaStatusAsync(token, ct) : null;
        return status is null ? TypedResults.Unauthorized() : TypedResults.Ok(status);
    }

    private static async Task<Results<Ok<MfaEnrollmentStart>, UnauthorizedHttpResult, Conflict<MessageResponse>>> BeginEnrollment(
        HttpContext http, IAuthService auth, CancellationToken ct)
    {
        if (SessionToken(http) is not { } token || await auth.GetMfaStatusAsync(token, ct) is not { Pending: false } status)
        {
            return TypedResults.Unauthorized();
        }

        if (status.Enrolled)
        {
            return TypedResults.Conflict(new MessageResponse("A factor is already enrolled; resetting it is a support flow."));
        }

        var start = await auth.BeginMfaEnrollmentAsync(token, ct);
        return start is null ? TypedResults.Unauthorized() : TypedResults.Ok(start);
    }

    private static async Task<Results<Ok<RecoveryCodesResponse>, BadRequest<MessageResponse>>> ConfirmEnrollment(
        MfaCodeRequest request, HttpContext http, IAuthService auth, CancellationToken ct)
    {
        var codes = SessionToken(http) is { } token
            ? await auth.ConfirmMfaEnrollmentAsync(token, request.Code, ct)
            : null;
        return codes is null
            ? TypedResults.BadRequest(new MessageResponse("Invalid code or no enrollment in progress."))
            : TypedResults.Ok(new RecoveryCodesResponse(
                "MFA enabled. Store these one-time recovery codes now — they are not shown again.", codes));
    }

    private static async Task<Results<Ok<MessageResponse>, UnauthorizedHttpResult>> Challenge(
        MfaCodeRequest request, HttpContext http, IAuthService auth, CancellationToken ct)
    {
        var ok = SessionToken(http) is { } token && await auth.CompleteMfaChallengeAsync(token, request.Code, ct);
        return ok ? TypedResults.Ok(new MessageResponse("Logged in.")) : TypedResults.Unauthorized();
    }

    private static async Task<Results<Ok<MessageResponse>, UnauthorizedHttpResult>> StepUp(
        MfaCodeRequest request, HttpContext http, IAuthService auth, CancellationToken ct)
    {
        var ok = SessionToken(http) is { } token && await auth.StepUpAsync(token, request.Code, ct);
        return ok ? TypedResults.Ok(new MessageResponse("Strong authentication refreshed.")) : TypedResults.Unauthorized();
    }

    private static string? SessionToken(HttpContext http) =>
        http.Request.Cookies.TryGetValue(AuthEndpoints.SessionCookieName, out var token) && token.Length > 0 ? token : null;
}

public record MfaCodeRequest([property: Required, MaxLength(64)] string Code);

public record RecoveryCodesResponse(string Message, IReadOnlyList<string> RecoveryCodes);

/// <summary>Enums are numeric on the wire (AGENTS.md invariant).</summary>
public record MfaPolicyResponse(MfaRequirement PlatformMinimum, MfaRequirement TenantPolicy, MfaRequirement Effective);

public record SetMfaPolicyRequest(MfaRequirement Policy);
