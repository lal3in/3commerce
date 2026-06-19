using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Identity.Domain;
using ThreeCommerce.Identity.Domain.Authz;
using ThreeCommerce.Identity.Infrastructure;

namespace ThreeCommerce.Identity.Api.Endpoints;

public static class AdminRbacEndpoints
{
    public static IEndpointRouteBuilder MapAdminRbac(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/rbac")
            .WithTags("Admin RBAC")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);

        group.MapGet("/permissions", ListPermissions);
        group.MapGet("/roles", ListRoles);
        group.MapPost("/roles", CreateRole);
        group.MapPut("/roles/{id:guid}/permissions", SetRolePermissions);
        group.MapPost("/memberships/{membershipId:guid}/roles/{roleId:guid}", AssignRole);
        group.MapDelete("/memberships/{membershipId:guid}/roles/{roleId:guid}", RemoveRole);
        group.MapGet("/principals/{principalId:guid}/effective-permissions", EffectivePermissions);

        return app;
    }

    private static async Task<Ok<List<PermissionResponse>>> ListPermissions(
        IdentityDbContext db, CancellationToken cancellationToken)
    {
        var items = await db.Permissions.AsNoTracking()
            .OrderBy(p => p.Key)
            .Select(p => new PermissionResponse(p.Key, p.Description, p.RiskLevel))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(items);
    }

    private static async Task<Ok<List<RoleResponse>>> ListRoles(
        Guid tenantId, IdentityDbContext db, CancellationToken cancellationToken)
    {
        var roles = await db.Roles.AsNoTracking()
            .Include(r => r.Permissions)
            .Where(r => r.TenantId == tenantId)
            .OrderBy(r => r.Name)
            .Select(r => ToResponse(r))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(roles);
    }

    private static async Task<Results<Created<RoleResponse>, Conflict<string>, ValidationProblem>> CreateRole(
        CreateRoleRequest request,
        IdentityDbContext db,
        CancellationToken cancellationToken)
    {
        if (await db.Roles.AnyAsync(r => r.TenantId == request.TenantId && r.Key == request.Key, cancellationToken))
        {
            return TypedResults.Conflict($"Role key '{request.Key}' already exists for this tenant.");
        }

        try
        {
            RbacRules.EnsureKnownPermissions(request.PermissionKeys ?? []);
        }
        catch (DomainRuleException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.PermissionKeys)] = [ex.Message] });
        }

        var role = new Role
        {
            Id = Guid.CreateVersion7(),
            TenantId = request.TenantId,
            Key = request.Key,
            Name = request.Name,
            Description = request.Description,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        RbacRules.SetPermissions(role, request.PermissionKeys ?? []);
        db.Roles.Add(role);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/admin/rbac/roles/{role.Id}", ToResponse(role));
    }

    private static async Task<Results<Ok<RoleResponse>, NotFound, ValidationProblem>> SetRolePermissions(
        Guid id,
        SetRolePermissionsRequest request,
        IdentityDbContext db,
        RbacManagementService rbac,
        CancellationToken cancellationToken)
    {
        if (!await db.Roles.AnyAsync(r => r.Id == id, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        try
        {
            await rbac.SetRolePermissionsAsync(id, request.PermissionKeys, cancellationToken);
        }
        catch (DomainRuleException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.PermissionKeys)] = [ex.Message] });
        }

        var role = await db.Roles.AsNoTracking().Include(r => r.Permissions).SingleAsync(r => r.Id == id, cancellationToken);
        return TypedResults.Ok(ToResponse(role));
    }

    private static async Task<NoContent> AssignRole(
        Guid membershipId, Guid roleId, RbacManagementService rbac, CancellationToken cancellationToken)
    {
        await rbac.AssignRoleAsync(membershipId, roleId, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> RemoveRole(
        Guid membershipId, Guid roleId, RbacManagementService rbac, CancellationToken cancellationToken)
    {
        await rbac.RemoveRoleAsync(membershipId, roleId, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<EffectivePermissionsResponse>, NotFound>> EffectivePermissions(
        Guid principalId, Guid tenantId, PolicyDecisionService policy, CancellationToken cancellationToken)
    {
        var context = await policy.ResolveContextAsync(principalId, tenantId, cancellationToken);
        return context is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(new EffectivePermissionsResponse(principalId, tenantId, context.GrantedPermissions.OrderBy(p => p).ToList()));
    }

    private static RoleResponse ToResponse(Role role) => new(
        role.Id,
        role.TenantId,
        role.Key,
        role.Name,
        role.Description,
        role.IsBuiltIn,
        role.IsSystem,
        role.Permissions.Select(p => p.PermissionKey).OrderBy(p => p).ToList());
}

public sealed record PermissionResponse(string Key, string Description, PermissionRiskLevel RiskLevel);

public sealed record RoleResponse(
    Guid Id,
    Guid? TenantId,
    string Key,
    string Name,
    string? Description,
    bool IsBuiltIn,
    bool IsSystem,
    IReadOnlyList<string> PermissionKeys);

public sealed record CreateRoleRequest(
    [property: Required] Guid TenantId,
    [property: Required, RegularExpression("^[a-z0-9-]+$")] string Key,
    [property: Required] string Name,
    string? Description,
    IReadOnlyList<string>? PermissionKeys);

public sealed record SetRolePermissionsRequest([property: Required] IReadOnlyList<string> PermissionKeys);

public sealed record EffectivePermissionsResponse(Guid PrincipalId, Guid TenantId, IReadOnlyList<string> PermissionKeys);
