using System.Net;
using System.Net.Http.Json;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Identity.Api.Endpoints;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Provisioning a supplier-portal login (aui): an admin turns a user into a supplier bound to a
/// supplier entity, and the binding then flows into the session claims — introspection reports the
/// supplier role plus the supplier_entity id the gateway mints for services to scope on.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase2Collection.Name)]
public class SupplierLoginProvisioningTests(Phase2Fixture fixture)
{
    private static readonly Guid DefaultTenant = new("00000000-0000-0000-0000-000000000001");

    private sealed record Introspected(Guid UserId, string Role, Guid? SupplierEntityId, bool EmailVerified);

    private static string SessionCookie(HttpResponseMessage response)
    {
        var setCookie = response.Headers.GetValues("Set-Cookie")
            .First(c => c.StartsWith(AuthEndpoints.SessionCookieName, StringComparison.Ordinal));
        return setCookie.Split(';')[0].Split('=', 2)[1];
    }

    private async Task<Introspected> IntrospectAsync(HttpClient client, string email, string password)
    {
        var login = await client.PostAsJsonAsync("/login", new { email, password });
        login.EnsureSuccessStatusCode();
        var introspect = await client.PostAsJsonAsync("/internal/introspection", new { token = SessionCookie(login) });
        introspect.EnsureSuccessStatusCode();
        return (await introspect.Content.ReadFromJsonAsync<Introspected>())!;
    }

    [Fact]
    public async Task Admin_makes_a_user_a_supplier_and_the_binding_flows_into_the_claims()
    {
        using var identity = fixture.CreateIdentityFactory();
        using var client = identity.CreateClient(new() { HandleCookies = false });
        var email = $"supplier-{Guid.NewGuid():N}@example.test";
        var supplierEntityId = Guid.NewGuid();

        await client.PostAsJsonAsync("/register", new { email, password = "a-strong-password" });

        // Starts life as an ordinary, unverified customer.
        var before = await IntrospectAsync(client, email, "a-strong-password");
        Assert.Equal("customer", before.Role);
        Assert.Null(before.SupplierEntityId);

        // Admin turns them into a supplier bound to the entity.
        using var admin = identity.CreateClient();
        admin.DefaultRequestHeaders.Add(
            InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.CreateVersion7(), "admin", tenantId: DefaultTenant.ToString()));
        var make = await admin.PostAsJsonAsync(
            $"/admin/users/{before.UserId}/make-supplier?tenantId={DefaultTenant}", new { supplierEntityId });
        Assert.Equal(HttpStatusCode.NoContent, make.StatusCode);

        // Re-login (the old session was invalidated by the ClaimsVersion bump) — the claims now carry
        // the supplier role, the binding, and a verified email (operator-provisioned).
        var after = await IntrospectAsync(client, email, "a-strong-password");
        Assert.Equal(InternalClaimsAuth.SupplierRole, after.Role);
        Assert.Equal(supplierEntityId, after.SupplierEntityId);
        Assert.True(after.EmailVerified);
    }

    [Fact]
    public async Task Make_supplier_on_a_foreign_tenant_is_forbidden()
    {
        using var identity = fixture.CreateIdentityFactory();
        using var admin = identity.CreateClient();
        admin.DefaultRequestHeaders.Add(
            InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.CreateVersion7(), "admin", tenantId: DefaultTenant.ToString()));

        // A tenant admin whose claim names the default tenant may not act on another tenant's users.
        var foreign = Guid.NewGuid();
        var response = await admin.PostAsJsonAsync(
            $"/admin/users/{Guid.NewGuid()}/make-supplier?tenantId={foreign}", new { supplierEntityId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
