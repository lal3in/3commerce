using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Tenancy;
using ThreeCommerce.Identity.Domain;
using ThreeCommerce.Identity.Infrastructure;

namespace ThreeCommerce.Identity.Api;

/// <summary>
/// Development convenience only: seeds one admin user from config so admin
/// endpoints are testable. No-ops outside Development or without config.
/// </summary>
public static class DevAdminSeeder
{
    public static async Task SeedAsync(WebApplication app)
    {
        var email = app.Configuration["Identity:SeedAdmin:Email"];
        var password = app.Configuration["Identity:SeedAdmin:Password"];
        if (!app.Environment.IsDevelopment() || string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            return;
        }

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var bootstrapper = scope.ServiceProvider.GetRequiredService<IdentityBootstrapper>();

        if (!await db.Database.CanConnectAsync())
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var tenant = await bootstrapper.EnsureDefaultTenantAsync(default);

        // Seed under the tenant scope so the Users FORCE-RLS WITH CHECK passes (ADR-0024) when the
        // service runs as the non-superuser identity_svc.
        await db.RunInTenantScopeAsync(TenantContext.ForTenant(tenant.Id), async () =>
        {
            if (await db.Users.AnyAsync(u => u.Role == Roles.Admin))
            {
                return;
            }

            await bootstrapper.SeedPermissionRegistryAsync(tenant.Id, default);
            var principal = new ThreeCommerce.Identity.Domain.Tenancy.Principal
            {
                Id = Guid.CreateVersion7(),
                Type = ThreeCommerce.Identity.Domain.Tenancy.PrincipalType.Human,
                DisplayName = email.ToLowerInvariant(),
                IsPlatformAdmin = true,
                CreatedAt = now,
            };
            db.Principals.Add(principal);
            db.Users.Add(new User
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                PrincipalId = principal.Id,
                Email = email.ToLowerInvariant(),
                PasswordHash = hasher.Hash(password),
                EmailVerified = true,
                Role = Roles.Admin,
                CreatedAt = now,
            });
            var membership = await bootstrapper.EnsureMembershipAsync(
                tenant.Id,
                principal,
                ThreeCommerce.Identity.Domain.Tenancy.MembershipKind.Staff,
                isTenantOwner: true,
                default);
            await bootstrapper.AssignRoleAsync(membership, Roles.Admin, default);
            await db.SaveChangesAsync(default);
            app.Logger.LogWarning("DEV: seeded admin user {Email}", email);
        });
    }
}
