using System.Security.Claims;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;

namespace ThreeCommerce.Identity.Tests;

/// <summary>
/// The supplier self-scope guard (InternalClaimsAuth): an operator (admin/master) may act on any
/// supplier, but a supplier login may only touch the entity named in its supplier_entity claim.
/// </summary>
public class SupplierClaimAuthTests
{
    private static readonly Guid Own = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private static ClaimsPrincipal Principal(string role, Guid? supplierEntity)
    {
        var claims = new List<Claim> { new("role", role) };
        if (supplierEntity is { } e)
        {
            claims.Add(new Claim("supplier_entity", e.ToString()));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test", "sub", "role"));
    }

    [Fact]
    public void Supplier_can_act_only_on_its_own_entity()
    {
        var supplier = Principal(InternalClaimsAuth.SupplierRole, Own);
        Assert.True(InternalClaimsAuth.CanActForSupplier(supplier, Own));
        Assert.False(InternalClaimsAuth.CanActForSupplier(supplier, Other));
    }

    [Fact]
    public void Operators_can_act_on_any_supplier()
    {
        Assert.True(InternalClaimsAuth.CanActForSupplier(Principal("admin", null), Own));
        Assert.True(InternalClaimsAuth.CanActForSupplier(Principal(InternalClaimsAuth.MasterRole, null), Other));
    }

    [Fact]
    public void A_role_without_a_matching_binding_is_denied()
    {
        Assert.False(InternalClaimsAuth.CanActForSupplier(Principal("customer", null), Own));
        // A supplier login with no binding claim can't reach anyone's entity.
        Assert.False(InternalClaimsAuth.CanActForSupplier(Principal(InternalClaimsAuth.SupplierRole, null), Own));
    }

    [Fact]
    public void SupplierEntityId_reads_the_binding_claim()
    {
        Assert.Equal(Own, InternalClaimsAuth.SupplierEntityId(Principal(InternalClaimsAuth.SupplierRole, Own)));
        Assert.Null(InternalClaimsAuth.SupplierEntityId(Principal("admin", null)));
    }
}
