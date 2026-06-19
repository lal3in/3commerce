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
        group.MapPut("/", UpdateProfile);
        group.MapGet("/addresses", ListAddresses);
        group.MapPost("/addresses", AddAddress);
        group.MapPut("/addresses/{id:guid}", UpdateAddress);
        group.MapDelete("/addresses/{id:guid}", DeleteAddress);

        return app;
    }

    private static Guid UserId(ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue("sub")!);

    // The authenticated user's tenant, carried in the internal claims (gateway mints it from
    // introspection). Used to scope FORCE-RLS Users/Addresses reads/writes (ADR-0024).
    private static TenantContext Scope(ClaimsPrincipal principal) =>
        TenantContext.ForTenant(Guid.Parse(principal.FindFirstValue("tenant")!), UserId(principal));

    private static async Task<Results<Ok<ProfileResponse>, NotFound>> GetProfile(
        ClaimsPrincipal principal, IdentityDbContext db, CancellationToken cancellationToken)
    {
        var userId = UserId(principal);
        var user = await db.RunInTenantScopeAsync(Scope(principal),
            () => db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, cancellationToken));
        return user is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(new ProfileResponse(user.Id, user.Email, user.GivenName, user.FamilyName, user.EmailVerified, user.CreatedAt));
    }

    private static async Task<Results<NoContent, NotFound>> UpdateProfile(
        ProfileRequest request, ClaimsPrincipal principal, IdentityDbContext db, CancellationToken cancellationToken)
    {
        var userId = UserId(principal);
        return await db.RunInTenantScopeAsync(Scope(principal), async () =>
        {
            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (user is null)
            {
                return (Results<NoContent, NotFound>)TypedResults.NotFound();
            }

            user.GivenName = NormalizeOptional(request.GivenName);
            user.FamilyName = NormalizeOptional(request.FamilyName);
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.NoContent();
        });
    }

    private static async Task<Ok<List<AddressResponse>>> ListAddresses(
        ClaimsPrincipal principal, IdentityDbContext db, CancellationToken cancellationToken)
    {
        var userId = UserId(principal);
        var addresses = await db.RunInTenantScopeAsync(Scope(principal),
            () => db.Addresses.AsNoTracking()
                .Where(a => a.UserId == userId)
                .OrderByDescending(a => a.IsDefault)
                .ThenBy(a => a.Name)
                .Select(a => new AddressResponse(a.Id, a.Purpose, a.IsDefault, a.Name, a.Line1, a.Line2, a.City, a.Postcode, a.Country))
                .ToListAsync(cancellationToken));
        return TypedResults.Ok(addresses);
    }

    private static async Task<Created<AddressResponse>> AddAddress(
        AddressRequest request, ClaimsPrincipal principal, IdentityDbContext db, CancellationToken cancellationToken)
    {
        var scope = Scope(principal);
        var userId = UserId(principal);
        var address = new Address
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            TenantId = scope.TenantId!.Value,
            Purpose = request.Purpose,
            IsDefault = request.IsDefault,
            Name = request.Name,
            Line1 = request.Line1,
            Line2 = request.Line2,
            City = request.City,
            Postcode = request.Postcode,
            Country = request.Country.ToUpperInvariant(),
        };
        await db.RunInTenantScopeAsync(scope, async () =>
        {
            db.Addresses.Add(address);
            if (address.IsDefault)
            {
                await ClearConflictingDefaultsAsync(db, userId, address.Purpose, address.Id, cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
        });

        return TypedResults.Created($"/me/addresses/{address.Id}", ToResponse(address));
    }

    private static async Task<Results<NoContent, NotFound>> UpdateAddress(
        Guid id, AddressRequest request, ClaimsPrincipal principal, IdentityDbContext db, CancellationToken cancellationToken)
    {
        var userId = UserId(principal);
        return await db.RunInTenantScopeAsync(Scope(principal), async () =>
        {
            // Ownership scoping: someone else's address id is a 404, not a 403 (no existence leaks).
            var address = await db.Addresses.SingleOrDefaultAsync(a => a.Id == id && a.UserId == userId, cancellationToken);
            if (address is null)
            {
                return (Results<NoContent, NotFound>)TypedResults.NotFound();
            }

            address.Purpose = request.Purpose;
            address.IsDefault = request.IsDefault;
            address.Name = request.Name;
            address.Line1 = request.Line1;
            address.Line2 = request.Line2;
            address.City = request.City;
            address.Postcode = request.Postcode;
            address.Country = request.Country.ToUpperInvariant();
            if (address.IsDefault)
            {
                await ClearConflictingDefaultsAsync(db, userId, address.Purpose, address.Id, cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.NoContent();
        });
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAddress(
        Guid id, ClaimsPrincipal principal, IdentityDbContext db, CancellationToken cancellationToken)
    {
        var userId = UserId(principal);
        return await db.RunInTenantScopeAsync(Scope(principal), async () =>
        {
            var address = await db.Addresses.SingleOrDefaultAsync(a => a.Id == id && a.UserId == userId, cancellationToken);
            if (address is null)
            {
                return (Results<NoContent, NotFound>)TypedResults.NotFound();
            }

            db.Addresses.Remove(address);
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.NoContent();
        });
    }

    private static async Task ClearConflictingDefaultsAsync(
        IdentityDbContext db,
        Guid userId,
        AddressPurpose purpose,
        Guid keepAddressId,
        CancellationToken cancellationToken)
    {
        var addresses = await db.Addresses
            .Where(a => a.UserId == userId && a.Id != keepAddressId && a.IsDefault)
            .ToListAsync(cancellationToken);
        foreach (var address in addresses.Where(a => AddressDefaultRules.DefaultsConflict(a.Purpose, purpose)))
        {
            address.IsDefault = false;
        }
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static AddressResponse ToResponse(Address address) =>
        new(address.Id, address.Purpose, address.IsDefault, address.Name, address.Line1, address.Line2, address.City, address.Postcode, address.Country);
}

public record ProfileRequest(
    [property: MaxLength(100)] string? GivenName,
    [property: MaxLength(100)] string? FamilyName);

public record ProfileResponse(Guid Id, string Email, string? GivenName, string? FamilyName, bool EmailVerified, DateTimeOffset CreatedAt);

public record AddressRequest(
    AddressPurpose Purpose,
    bool IsDefault,
    [property: Required, MaxLength(100)] string Name,
    [property: Required, MaxLength(200)] string Line1,
    [property: MaxLength(200)] string? Line2,
    [property: Required, MaxLength(100)] string City,
    [property: Required, MaxLength(20)] string Postcode,
    [property: Required, StringLength(2, MinimumLength = 2)] string Country);

public record AddressResponse(Guid Id, AddressPurpose Purpose, bool IsDefault, string Name, string Line1, string? Line2, string City, string Postcode, string Country);
