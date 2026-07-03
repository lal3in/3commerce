using System.Net;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Identity.Domain;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Cross-tenant guard on the master-admin user endpoints (aui_8 note / review rev_9): a tenant
/// admin may only manage users in the tenant their gateway-minted claim names; a foreign tenantId
/// is 403. The platform-scope "master" role crosses tenants.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase2Collection.Name)]
public class AdminUserTenantGateTests(Phase2Fixture fixture) : IAsyncLifetime
{
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<ThreeCommerce.Identity.Api.IApiMarker> _identity = null!;

    public Task InitializeAsync()
    {
        _identity = fixture.CreateIdentityFactory();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _identity.Dispose();
        return Task.CompletedTask;
    }

    private HttpClient ClientFor(string role, Guid tenant)
    {
        var client = _identity.CreateClient();
        client.DefaultRequestHeaders.Add(
            InternalClaimsAuth.HeaderName,
            fixture.MintInternalClaims(Guid.CreateVersion7(), role, tenantId: tenant.ToString()));
        return client;
    }

    [Fact]
    public async Task Tenant_admin_cannot_touch_another_tenants_users()
    {
        var ownTenant = Guid.CreateVersion7();
        var foreignTenant = Guid.CreateVersion7();
        using var admin = ClientFor(Roles.Admin, ownTenant);

        var foreign = await admin.GetAsync($"/admin/users?tenantId={foreignTenant}");
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);

        var reset = await admin.PostAsync($"/admin/users/{Guid.CreateVersion7()}/reset-password?tenantId={foreignTenant}", null);
        Assert.Equal(HttpStatusCode.Forbidden, reset.StatusCode);

        // Own tenant remains fully allowed (empty list is fine — the gate is what's under test).
        var own = await admin.GetAsync($"/admin/users?tenantId={ownTenant}");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    [Fact]
    public async Task Master_role_crosses_tenants()
    {
        var anyTenant = Guid.CreateVersion7();
        using var master = ClientFor(InternalClaimsAuth.MasterRole, Guid.CreateVersion7());

        var response = await master.GetAsync($"/admin/users?tenantId={anyTenant}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
