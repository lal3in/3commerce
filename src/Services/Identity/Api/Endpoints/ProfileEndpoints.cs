using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.BuildingBlocks.Infrastructure.Tenancy;
using ThreeCommerce.Identity.Domain;
using ThreeCommerce.Identity.Infrastructure;

namespace ThreeCommerce.Identity.Api.Endpoints;

public static class ProfileEndpoints
{
    public static IEndpointRouteBuilder MapProfile(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/me")
            .WithTags("Profile")
            .RequireAuthorization(InternalClaimsAuth.CustomerPolicy);

        group.MapGet("/", GetProfile);
        group.MapGet("/addresses", ListAddresses);
        group.MapPost("/addresses", AddAddress);
        group.MapPut("/addresses/{id:guid}", UpdateAddress);
        group.MapDelete("/addresses/{id:guid}", DeleteAddress);

        return app;
    }

    private static Guid UserId(ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue("sub")!);

    private static async Task<Results<Ok<ProfileResponse>, NotFound>> GetProfile(
        ClaimsPrincipal principal, IdentityDbContext db, CancellationToken cancellationToken)
    {
        var userId = UserId(principal);
        // Users is FORCE-RLS (ADR-0024); the internal claims don't carry a tenant id, so the
        // authenticated self-read runs under platform scope (the sub is verified), like
        // introspection. (Carrying the tenant in the internal claims would let this be tenant-scoped.)
        var user = await db.RunInTenantScopeAsync(TenantContext.Platform(),
            () => db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, cancellationToken));
        return user is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(new ProfileResponse(user.Id, user.Email, user.EmailVerified, user.CreatedAt));
    }

    private static async Task<Ok<List<AddressResponse>>> ListAddresses(
        ClaimsPrincipal principal, IdentityDbContext db, CancellationToken cancellationToken)
    {
        var userId = UserId(principal);
        var addresses = await db.Addresses.AsNoTracking()
            .Where(a => a.UserId == userId)
            .Select(a => new AddressResponse(a.Id, a.Name, a.Line1, a.Line2, a.City, a.Postcode, a.Country))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(addresses);
    }

    private static async Task<Created<AddressResponse>> AddAddress(
        AddressRequest request, ClaimsPrincipal principal, IdentityDbContext db, CancellationToken cancellationToken)
    {
        var address = new Address
        {
            Id = Guid.CreateVersion7(),
            UserId = UserId(principal),
            Name = request.Name,
            Line1 = request.Line1,
            Line2 = request.Line2,
            City = request.City,
            Postcode = request.Postcode,
            Country = request.Country,
        };
        db.Addresses.Add(address);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created($"/me/addresses/{address.Id}",
            new AddressResponse(address.Id, address.Name, address.Line1, address.Line2, address.City, address.Postcode, address.Country));
    }

    private static async Task<Results<NoContent, NotFound>> UpdateAddress(
        Guid id, AddressRequest request, ClaimsPrincipal principal, IdentityDbContext db, CancellationToken cancellationToken)
    {
        var userId = UserId(principal);
        // Ownership scoping: someone else's address id is a 404, not a 403 (no existence leaks).
        var address = await db.Addresses.SingleOrDefaultAsync(a => a.Id == id && a.UserId == userId, cancellationToken);
        if (address is null)
        {
            return TypedResults.NotFound();
        }

        address.Name = request.Name;
        address.Line1 = request.Line1;
        address.Line2 = request.Line2;
        address.City = request.City;
        address.Postcode = request.Postcode;
        address.Country = request.Country;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAddress(
        Guid id, ClaimsPrincipal principal, IdentityDbContext db, CancellationToken cancellationToken)
    {
        var userId = UserId(principal);
        var address = await db.Addresses.SingleOrDefaultAsync(a => a.Id == id && a.UserId == userId, cancellationToken);
        if (address is null)
        {
            return TypedResults.NotFound();
        }

        db.Addresses.Remove(address);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}

public record ProfileResponse(Guid Id, string Email, bool EmailVerified, DateTimeOffset CreatedAt);

public record AddressRequest(
    [property: Required, MaxLength(100)] string Name,
    [property: Required, MaxLength(200)] string Line1,
    [property: MaxLength(200)] string? Line2,
    [property: Required, MaxLength(100)] string City,
    [property: Required, MaxLength(20)] string Postcode,
    [property: Required, StringLength(2, MinimumLength = 2)] string Country);

public record AddressResponse(Guid Id, string Name, string Line1, string? Line2, string City, string Postcode, string Country);
