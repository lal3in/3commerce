using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Tax;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.BuildingBlocks.Infrastructure.Tenancy;
using ThreeCommerce.Identity.Domain;
using ThreeCommerce.Identity.Domain.Authz;
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
        group.MapGet("/permissions", GetPermissions)
            .WithSummary("The caller's effective permission keys in their tenant (advisory UI capability metadata, ADR-0025; services re-decide every action).");
        group.MapGet("/addresses", ListAddresses);
        group.MapPost("/addresses", AddAddress);
        group.MapPut("/addresses/{id:guid}", UpdateAddress);
        group.MapDelete("/addresses/{id:guid}", DeleteAddress);
        group.MapGet("/business-identity", GetBusinessIdentity)
            .WithSummary("The caller's saved default business identity (ADR-0063 P3b) — 204 when none is saved.");
        group.MapPut("/business-identity", SaveBusinessIdentity)
            .WithSummary("Save the default business identity that prefills checkout's Business purchase block; validated per tax-ID scheme like checkout.");
        group.MapDelete("/business-identity", ClearBusinessIdentity)
            .WithSummary("Clear the saved default business identity; placed orders keep their own snapshot.");

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
            : TypedResults.Ok(new ProfileResponse(
                user.Id, user.Email, user.Title, user.FirstName, user.MiddleName, user.LastName, user.PreferredName,
                user.Phone, user.DateOfBirth, user.MarketingConsent, user.EmailVerified, user.CreatedAt,
                BusinessIdentityResponse.From(user.BusinessIdentity)));
    }

    /// <summary>
    /// The caller's effective permissions (refund_permission_enforcement): the admin uses this to hide actions the
    /// caller cannot take — advisory only, every service re-decides through the PDP (ADR-0025). A platform admin
    /// (MasterGlobal) is granted every registered permission. No principal behind the user → an empty set.
    /// </summary>
    private static async Task<Ok<MyPermissionsResponse>> GetPermissions(
        ClaimsPrincipal principal, PolicyDecisionService policy, CancellationToken cancellationToken)
    {
        var context = await policy.ResolveUserContextAsync(
            UserId(principal), Guid.Parse(principal.FindFirstValue("tenant")!), cancellationToken);
        if (context is null)
        {
            return TypedResults.Ok(new MyPermissionsResponse(false, []));
        }

        var keys = context.IsPlatformAdmin
            ? PermissionRegistry.AllPermissionKeys
            : PermissionRegistry.AllPermissionKeys.Where(context.Has).ToArray();
        return TypedResults.Ok(new MyPermissionsResponse(context.IsPlatformAdmin, keys));
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

            user.Title = NormalizeOptional(request.Title);
            user.FirstName = NormalizeOptional(request.FirstName);
            user.MiddleName = NormalizeOptional(request.MiddleName);
            user.LastName = NormalizeOptional(request.LastName);
            user.PreferredName = NormalizeOptional(request.PreferredName);
            user.Phone = NormalizeOptional(request.Phone);
            user.DateOfBirth = request.DateOfBirth;
            user.MarketingConsent = request.MarketingConsent;
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
                .Select(a => new AddressResponse(a.Id, a.Purpose, a.IsDefault, a.Name, a.Line1, a.Line2, a.City, a.Region, a.Postcode, a.Country))
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
            Region = string.IsNullOrWhiteSpace(request.Region) ? null : request.Region.Trim(),
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
            address.Region = string.IsNullOrWhiteSpace(request.Region) ? null : request.Region.Trim();
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

    /// <summary>
    /// The caller's saved default business identity (ADR-0063 P3b): 200 with it, or 204 when none is saved.
    /// </summary>
    private static async Task<Results<Ok<BusinessIdentityResponse>, NoContent, NotFound>> GetBusinessIdentity(
        ClaimsPrincipal principal, IdentityDbContext db, CancellationToken cancellationToken)
    {
        var userId = UserId(principal);
        var user = await db.RunInTenantScopeAsync(Scope(principal),
            () => db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, cancellationToken));
        if (user is null)
        {
            return TypedResults.NotFound();
        }

        return BusinessIdentityResponse.From(user.BusinessIdentity) is { } identity
            ? TypedResults.Ok(identity)
            : TypedResults.NoContent();
    }

    /// <summary>
    /// Saves the caller's default business identity, validated and normalized with the same per-scheme rules checkout
    /// applies (ADR-0063 P3b). Returns the normalized identity, or 400 problem+json whose <c>field</c> extension and
    /// <c>errors</c> key name the refused input (<c>companyName</c> / <c>taxId</c> / <c>taxIdScheme</c> /
    /// <c>taxIdJurisdiction</c>). Nothing is saved on a refusal.
    /// </summary>
    private static async Task<Results<Ok<BusinessIdentityResponse>, ValidationProblem, NotFound>> SaveBusinessIdentity(
        BusinessIdentityRequest request, ClaimsPrincipal principal, IdentityDbContext db, CancellationToken cancellationToken)
    {
        BusinessIdentity identity;
        try
        {
            identity = BusinessIdentityRules.Resolve(request.CompanyName, request.TaxId, request.TaxIdScheme, request.TaxIdJurisdiction);
        }
        catch (BusinessIdentityException ex)
        {
            var field = JsonFieldName(ex.Field);
            return TypedResults.ValidationProblem(
                new Dictionary<string, string[]> { [field] = [ex.Message] },
                detail: ex.Message,
                title: "The business identity is invalid.",
                extensions: new Dictionary<string, object?> { ["field"] = field });
        }

        var userId = UserId(principal);
        return await db.RunInTenantScopeAsync(Scope(principal), async () =>
        {
            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (user is null)
            {
                return (Results<Ok<BusinessIdentityResponse>, ValidationProblem, NotFound>)TypedResults.NotFound();
            }

            user.SetBusinessIdentity(identity);
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.Ok(BusinessIdentityResponse.From(identity)!);
        });
    }

    /// <summary>Clears the caller's default business identity (ADR-0063 P3b). Orders already placed keep their own snapshot.</summary>
    private static async Task<Results<NoContent, NotFound>> ClearBusinessIdentity(
        ClaimsPrincipal principal, IdentityDbContext db, CancellationToken cancellationToken)
    {
        var userId = UserId(principal);
        return await db.RunInTenantScopeAsync(Scope(principal), async () =>
        {
            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (user is null)
            {
                return (Results<NoContent, NotFound>)TypedResults.NotFound();
            }

            user.SetBusinessIdentity(null);
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.NoContent();
        });
    }

    // The domain names a field in PascalCase (BusinessIdentityRules.*Field); the wire uses the request's camelCase.
    private static string JsonFieldName(string field) =>
        field.Length == 0 ? field : char.ToLowerInvariant(field[0]) + field[1..];

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static AddressResponse ToResponse(Address address) =>
        new(address.Id, address.Purpose, address.IsDefault, address.Name, address.Line1, address.Line2, address.City, address.Region, address.Postcode, address.Country);
}

/// <summary>The caller's effective permission keys (registry order) and whether they are a platform admin.</summary>
public record MyPermissionsResponse(bool IsPlatformAdmin, IReadOnlyList<string> Permissions);

public record ProfileRequest(
    [property: MaxLength(20)] string? Title,
    [property: MaxLength(100)] string? FirstName,
    [property: MaxLength(100)] string? MiddleName,
    [property: MaxLength(100)] string? LastName,
    [property: MaxLength(100)] string? PreferredName,
    [property: MaxLength(32)] string? Phone,
    DateOnly? DateOfBirth,
    bool MarketingConsent = false);

public record ProfileResponse(
    Guid Id, string Email, string? Title, string? FirstName, string? MiddleName, string? LastName, string? PreferredName,
    string? Phone, DateOnly? DateOfBirth, bool MarketingConsent, bool EmailVerified, DateTimeOffset CreatedAt,
    // ADR-0063 P3b (appended): the saved default business identity, null when none.
    BusinessIdentityResponse? BusinessIdentity = null);

/// <summary>
/// A default business identity to save (ADR-0063 P3b) — the shape of checkout's <c>buyer</c> block. The scheme is the
/// shared <see cref="TaxIdScheme"/> and crosses HTTP as a number. Bounds are checked by <see cref="BusinessIdentityRules"/>
/// so a refusal names the field (400 problem+json), never a generic validation error.
/// </summary>
public record BusinessIdentityRequest(
    string? CompanyName,
    string? TaxId = null,
    TaxIdScheme? TaxIdScheme = null,
    string? TaxIdJurisdiction = null);

/// <summary>The saved default business identity, normalized (tax ID compacted, jurisdiction the scheme implies).</summary>
public record BusinessIdentityResponse(string CompanyName, string? TaxId, TaxIdScheme? TaxIdScheme, string? TaxIdJurisdiction)
{
    public static BusinessIdentityResponse? From(BusinessIdentity? identity) =>
        identity is null ? null : new(identity.CompanyName, identity.TaxId, identity.TaxIdScheme, identity.TaxIdJurisdiction);
}

public record AddressRequest(
    AddressPurpose Purpose,
    bool IsDefault,
    [property: Required, MaxLength(100)] string Name,
    [property: Required, MaxLength(200)] string Line1,
    [property: MaxLength(200)] string? Line2,
    [property: Required, MaxLength(100)] string City,
    [property: Required, MaxLength(20)] string Postcode,
    [property: Required, StringLength(2, MinimumLength = 2)] string Country,
    // Sub-national region (state/province/county/…) — optional; not every country's address has one.
    [property: MaxLength(120)] string? Region = null);

public record AddressResponse(Guid Id, AddressPurpose Purpose, bool IsDefault, string Name, string Line1, string? Line2, string City, string? Region, string Postcode, string Country);
