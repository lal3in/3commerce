using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Support.Infrastructure;

namespace ThreeCommerce.Support.Api.Endpoints;

/// <summary>
/// Who may use an order-scoped customer endpoint (ADR-0018 amendment 2026-11-27): ONLY the user who placed the order,
/// as recorded on Support's local order copy (<see cref="Domain.OrderSnapshot.UserId"/>, fed by
/// <c>OrderConfirmed.UserId</c>). Every check fails CLOSED — no snapshot, a snapshot without an owner (guest order, or
/// projected before the field existed and not yet backfilled) or a caller without a <c>sub</c> all read as "not yours",
/// and the endpoint answers 404 so an order id never confirms that the order exists (as Ordering's own
/// <c>GET /orders/{id}</c> does).
/// </summary>
internal static class OrderOwnership
{
    /// <summary>The caller's Identity user id (the internal-claims <c>sub</c>), or null when it is missing or malformed.</summary>
    public static Guid? CallerId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue("sub"), out var id) ? id : null;

    /// <summary>An operator (admin / master role). Only the attachment download — which the Admin console reads through
    /// the customer route — lets an operator past the owner check; every other customer endpoint is owner-only.</summary>
    public static bool IsOperator(ClaimsPrincipal user) =>
        user.IsInRole("admin") || user.IsInRole(InternalClaimsAuth.MasterRole);

    /// <summary>Whether <paramref name="orderId"/> is the caller's order.</summary>
    public static async Task<bool> OwnsOrderAsync(SupportDbContext db, Guid orderId, ClaimsPrincipal user, CancellationToken ct)
    {
        var caller = CallerId(user);
        return caller is not null
            && await db.OrderSnapshots.AsNoTracking().AnyAsync(o => o.OrderId == orderId && o.UserId == caller, ct);
    }

    /// <summary>The ids of every order the caller owns — what the caller's ticket list is scoped to.</summary>
    public static IQueryable<Guid> OwnedOrderIds(SupportDbContext db, ClaimsPrincipal user)
    {
        var caller = CallerId(user);
        return caller is null
            ? db.OrderSnapshots.Where(_ => false).Select(o => o.OrderId)
            : db.OrderSnapshots.Where(o => o.UserId == caller).Select(o => o.OrderId);
    }

    /// <summary>The order a ticket or refund/return request belongs to (an attachment's owner), or null if there is none.</summary>
    public static async Task<Guid?> OrderOfAsync(SupportDbContext db, string ownerKind, Guid ownerId, CancellationToken ct) =>
        ownerKind switch
        {
            "ticket" => await db.Tickets.AsNoTracking().Where(t => t.Id == ownerId).Select(t => (Guid?)t.OrderId).SingleOrDefaultAsync(ct),
            "rma" => await db.RmaRequests.AsNoTracking().Where(r => r.Id == ownerId).Select(r => (Guid?)r.OrderId).SingleOrDefaultAsync(ct),
            _ => null,
        };
}
