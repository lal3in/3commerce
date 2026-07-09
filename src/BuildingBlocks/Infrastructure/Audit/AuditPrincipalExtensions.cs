using System.Security.Claims;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Audit;

/// <summary>
/// Builds an <see cref="AuditDraft"/> from the internal claims principal (mt6_1): the actor is the gateway
/// principal's <c>sub</c> (id) and <c>role</c> — the same claims InternalClaimsAuth validates. Keeps every
/// call site a one-liner: <c>await audit.RecordAsync(user.Mutation(tenantId, "Resource", id, "action"), ct)</c>.
/// </summary>
public static class AuditPrincipalExtensions
{
    /// <summary>The actor (id, role) from the internal claims principal — nulls when unauthenticated.</summary>
    public static (Guid? ActorId, string? ActorRole) AuditActor(this ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return (null, null);
        }

        var actorId = Guid.TryParse(principal.FindFirstValue("sub"), out var id) ? id : (Guid?)null;
        return (actorId, principal.FindFirstValue("role"));
    }

    /// <summary>A successful admin mutation performed by <paramref name="principal"/>.</summary>
    public static AuditDraft Mutation(
        this ClaimsPrincipal? principal, Guid tenantId, string resourceType, string resourceId, string action, string? summary = null)
    {
        var (actorId, actorRole) = principal.AuditActor();
        return AuditCategories.Mutation(tenantId, actorId, actorRole, resourceType, resourceId, action, summary);
    }

    /// <summary>A high-risk action <paramref name="principal"/> attempted that was denied — carries the reason.</summary>
    public static AuditDraft DeniedAttempt(
        this ClaimsPrincipal? principal, Guid tenantId, string resourceType, string resourceId, string action, string reason)
    {
        var (actorId, actorRole) = principal.AuditActor();
        return AuditCategories.DeniedAttempt(tenantId, actorId, actorRole, resourceType, resourceId, action, reason);
    }
}
