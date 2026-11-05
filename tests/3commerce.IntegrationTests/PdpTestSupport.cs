using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Infrastructure.Authz;
using ThreeCommerce.BuildingBlocks.Infrastructure.Tenancy;
using ThreeCommerce.Identity.Domain;
using ThreeCommerce.Identity.Domain.Tenancy;
using ThreeCommerce.Identity.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// The refund endpoints are PEPs that ask Identity's PDP (refund_permission_enforcement). In the fixtures that host
/// Payments / Support, their <see cref="PolicyDecisionClient"/> is routed to an in-process Identity host, and tests
/// seed staff users the way login does — a principal, a tenant user, a staff membership with a built-in role — so
/// the real decision chain (sub + tenant → principal → roles → permissions) is what answers.
/// </summary>
internal static class PdpTestSupport
{
    public static readonly Guid DefaultTenant = Guid.Parse("00000000-0000-0000-0000-000000000001");

    /// <summary>Sends the service's PDP calls to the (lazily created) in-process Identity host.</summary>
    public static void RoutePdpTo(IWebHostBuilder builder, Func<WebApplicationFactory<ThreeCommerce.Identity.Api.IApiMarker>> identity) =>
        builder.ConfigureTestServices(services => services.AddHttpClient<PolicyDecisionClient>()
            .ConfigurePrimaryHttpMessageHandler(() => identity().Server.CreateHandler()));

    /// <summary>
    /// Seeds a back-office user in the default tenant and returns its id (the internal-claims <c>sub</c>).
    /// <paramref name="roleKey"/> is a built-in RBAC role (admin / finance / support / ops …) or null for none;
    /// <paramref name="platformAdmin"/> gives the bootstrap-admin shape (MasterGlobal principal).
    /// </summary>
    public static async Task<Guid> SeedStaffUserAsync(
        WebApplicationFactory<ThreeCommerce.Identity.Api.IApiMarker> identity, string? roleKey, bool platformAdmin = false)
    {
        using var scope = identity.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var bootstrapper = new IdentityBootstrapper(db, TimeProvider.System);
        var userId = Guid.CreateVersion7();

        await db.RunInTenantScopeAsync(TenantContext.ForTenant(DefaultTenant), async () =>
        {
            var tenant = await bootstrapper.EnsureDefaultTenantAsync(default);
            await bootstrapper.SeedPermissionRegistryAsync(tenant.Id, default);
            var principal = new Principal
            {
                Id = Guid.CreateVersion7(),
                Type = PrincipalType.Human,
                DisplayName = $"{roleKey ?? "none"}-{userId:N}@example.test",
                IsPlatformAdmin = platformAdmin,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Principals.Add(principal);
            db.Users.Add(new User
            {
                Id = userId,
                TenantId = tenant.Id,
                PrincipalId = principal.Id,
                Email = principal.DisplayName,
                PasswordHash = "not-a-login",
                EmailVerified = true,
                Role = Roles.Admin, // the coarse role claim the gateway mints — never enough on its own
                CreatedAt = DateTimeOffset.UtcNow,
            });
            var membership = await bootstrapper.EnsureMembershipAsync(tenant.Id, principal, MembershipKind.Staff, isTenantOwner: false, default);
            if (roleKey is not null)
            {
                await bootstrapper.AssignRoleAsync(membership, roleKey, default);
            }

            await db.SaveChangesAsync();
        });

        return userId;
    }
}
