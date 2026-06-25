using System.Security.Claims;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Auth;

/// <summary>
/// Reads the signed-in customer's identity from the internal-claims principal for "/me" surfaces
/// (Phase 7 / mt7_6). The gateway mints `tenant` + `email` claims from the introspected session, so a
/// service scopes customer-owned data to these — never to a client-supplied parameter.
/// </summary>
public static class CustomerClaims
{
    public static bool TryRead(ClaimsPrincipal user, out Guid tenantId, out string email)
    {
        tenantId = Guid.Empty;
        email = string.Empty;

        var claimedEmail = user.FindFirstValue("email");
        if (string.IsNullOrWhiteSpace(claimedEmail) || !Guid.TryParse(user.FindFirstValue("tenant"), out var claimedTenant))
        {
            return false;
        }

        tenantId = claimedTenant;
        email = claimedEmail;
        return true;
    }
}
