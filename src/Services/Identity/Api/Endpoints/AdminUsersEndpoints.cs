using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
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
        group.MapPut("/{id:guid}/email", ChangeEmail).WithSummary("Change a user's email (re-verification required).");
        return app;
    }

    private static async Task<Ok<List<AdminUserDto>>> List(Guid tenantId, AdminUserService service, CancellationToken ct) =>
        TypedResults.Ok(await service.ListAsync(tenantId, ct));

    private static async Task<Results<Ok<ResetPasswordResponse>, NotFound>> ResetPassword(
        Guid id, Guid tenantId, AdminUserService service, CancellationToken ct)
    {
        var temporary = await service.ResetPasswordAsync(tenantId, id, ct);
        return temporary is null ? TypedResults.NotFound() : TypedResults.Ok(new ResetPasswordResponse(temporary));
    }

    private static async Task<Results<NoContent, BadRequest<string>>> ChangeEmail(
        Guid id, Guid tenantId, ChangeEmailRequest request, AdminUserService service, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@'))
        {
            return TypedResults.BadRequest("A valid email is required.");
        }

        return await service.ChangeEmailAsync(tenantId, id, request.Email, ct)
            ? TypedResults.NoContent()
            : TypedResults.BadRequest("Could not change email (user not found, or the email is already in use in this tenant).");
    }
}

public record ResetPasswordResponse(string TemporaryPassword);
public record ChangeEmailRequest([property: Required, EmailAddress] string Email);
