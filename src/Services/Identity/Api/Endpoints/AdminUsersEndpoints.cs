using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Identity.Infrastructure;

namespace ThreeCommerce.Identity.Api.Endpoints;

/// <summary>Operator user administration (aui_8): list users, reset a password, change an email. Admin-gated.</summary>
public static class AdminUsersEndpoints
{
    public static IEndpointRouteBuilder MapAdminUsers(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/users").WithTags("Admin Users")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);

        group.MapGet("/", List).WithSummary("List users in a tenant.");
        group.MapPost("/{id:guid}/reset-password", ResetPassword).WithSummary("Issue a one-time temporary password.");
        group.MapPost("/{id:guid}/verify-email", VerifyEmail).WithSummary("Manually mark a user's email verified (operator fallback; attaches prior guest orders).");
        group.MapPost("/{id:guid}/make-supplier", MakeSupplier).WithSummary("Turn a user into a supplier-portal login bound to a supplier entity.");
        group.MapPut("/{id:guid}/email", ChangeEmail).WithSummary("Change a user's email (re-verification required).");
        return app;
    }

    // Cross-tenant guard (rev_9): a tenant admin may only manage users in THEIR OWN tenant; other
    // tenants require the platform-scope master role. Without this, any tenant admin could reset
    // another tenant's passwords by passing a foreign tenantId.

    private static async Task<Results<Ok<List<AdminUserDto>>, ForbidHttpResult>> List(
        Guid tenantId, ClaimsPrincipal principal, AdminUserService service, CancellationToken ct) =>
        !InternalClaimsAuth.CanActForTenant(principal, tenantId)
            ? TypedResults.Forbid()
            : TypedResults.Ok(await service.ListAsync(tenantId, ct));

    private static async Task<Results<Ok<ResetPasswordResponse>, NotFound, ForbidHttpResult>> ResetPassword(
        Guid id, Guid tenantId, ClaimsPrincipal principal, AdminUserService service, CancellationToken ct)
    {
        if (!InternalClaimsAuth.CanActForTenant(principal, tenantId))
        {
            return TypedResults.Forbid();
        }

        var (actorId, actorRole) = principal.AuditActor();
        var temporary = await service.ResetPasswordAsync(tenantId, id, actorId, actorRole, ct);
        return temporary is null ? TypedResults.NotFound() : TypedResults.Ok(new ResetPasswordResponse(temporary));
    }

    private static async Task<Results<NoContent, NotFound, ForbidHttpResult>> VerifyEmail(
        Guid id, Guid tenantId, ClaimsPrincipal principal, AdminUserService service, CancellationToken ct)
    {
        if (!InternalClaimsAuth.CanActForTenant(principal, tenantId))
        {
            return TypedResults.Forbid();
        }

        var (actorId, actorRole) = principal.AuditActor();
        return await service.VerifyEmailAsync(tenantId, id, actorId, actorRole, ct)
            ? TypedResults.NoContent()
            : TypedResults.NotFound();
    }

    private static async Task<Results<NoContent, NotFound, ForbidHttpResult>> MakeSupplier(
        Guid id, Guid tenantId, MakeSupplierRequest request, ClaimsPrincipal principal, AdminUserService service, CancellationToken ct)
    {
        if (!InternalClaimsAuth.CanActForTenant(principal, tenantId))
        {
            return TypedResults.Forbid();
        }

        var (actorId, actorRole) = principal.AuditActor();
        return await service.MakeSupplierAsync(tenantId, id, request.SupplierEntityId, actorId, actorRole, ct)
            ? TypedResults.NoContent()
            : TypedResults.NotFound();
    }

    private static async Task<Results<NoContent, BadRequest<string>, ForbidHttpResult>> ChangeEmail(
        Guid id, Guid tenantId, ChangeEmailRequest request, ClaimsPrincipal principal, AdminUserService service, CancellationToken ct)
    {
        if (!InternalClaimsAuth.CanActForTenant(principal, tenantId))
        {
            return TypedResults.Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@'))
        {
            return TypedResults.BadRequest("A valid email is required.");
        }

        var (actorId, actorRole) = principal.AuditActor();
        return await service.ChangeEmailAsync(tenantId, id, request.Email, actorId, actorRole, ct)
            ? TypedResults.NoContent()
            : TypedResults.BadRequest("Could not change email (user not found, or the email is already in use in this tenant).");
    }
}

public record ResetPasswordResponse(string TemporaryPassword);
public record ChangeEmailRequest([property: Required, EmailAddress] string Email);
public record MakeSupplierRequest([property: Required] Guid SupplierEntityId);
