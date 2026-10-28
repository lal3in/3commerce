using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using MassTransit;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Entity.Domain;
using ThreeCommerce.Entity.Infrastructure;

namespace ThreeCommerce.Entity.Api.Endpoints;

/// <summary>
/// The tenant's seller of record and its establishments (ADR-0063 § 7.3, P2). The seller is the entity with
/// the TenantOwner profile — at most one per tenant; its tax identifiers are the ordinary entity identifiers
/// (<c>POST /entities/{id}/identifiers</c>). Every mutation records an audit entry and publishes
/// <c>LegalEntityChanged</c> in the same unit of work.
/// </summary>
public static class SellerIdentityEndpoints
{
    public static RouteGroupBuilder MapSellerIdentity(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/entities")
            .WithTags("Seller identity")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);

        group.MapGet("/seller", GetSeller)
            .WithSummary("The tenant's seller of record with its tax identifiers and establishments (404 when none is designated).");
        group.MapPost("/{id:guid}/seller", DesignateSeller)
            .WithSummary("Designate the entity as its tenant's seller of record (409 when another entity already is).")
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPost("/{id:guid}/establishments", AddEstablishment)
            .WithSummary("Add a seller establishment (address + tax jurisdiction) with a stable key storefronts bind to.")
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPut("/{id:guid}/establishments/{establishmentId:guid}", UpdateEstablishment)
            .WithSummary("Edit a seller establishment's label, address or jurisdiction (the key is immutable).")
            .ProducesProblem(StatusCodes.Status409Conflict);
        return group;
    }

    private static async Task<Results<Ok<SellerIdentityResponse>, NotFound>> GetSeller(
        Guid? tenantId,
        EntityDbContext db,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var tenant = tenantId ?? (Guid.TryParse(user.FindFirstValue("tenant"), out var claimed) ? claimed : Guid.Empty);
        var seller = await FindSellerAsync(db.Entities.AsNoTracking(), tenant, cancellationToken);
        return seller is null ? TypedResults.NotFound() : TypedResults.Ok(ToSellerResponse(seller));
    }

    private static async Task<Results<Ok<EntityDetailResponse>, NotFound, ValidationProblem, ProblemHttpResult>> DesignateSeller(
        Guid id,
        EntityDbContext db,
        IPublishEndpoint publish,
        AuditRecorder audit,
        ClaimsPrincipal user,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var entity = await EntityEndpoints.LoadWithDetailsAsync(db.Entities, id, cancellationToken);
        if (entity is null)
        {
            return TypedResults.NotFound();
        }

        if (entity.IsSeller)
        {
            return TypedResults.Ok(EntityEndpoints.ToDetail(entity)); // idempotent: nothing changed, nothing audited
        }

        // Serialize designations per tenant for the rest of the request transaction (TenantScopeMiddleware),
        // so two concurrent designations cannot both pass the "no other seller" check below.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext('entity-seller'), hashtext({entity.TenantId.ToString()}))", cancellationToken);
        var other = await FindSellerAsync(db.Entities.AsNoTracking(), entity.TenantId, cancellationToken);
        if (other is not null && other.Id != entity.Id)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "The tenant already has a seller of record.",
                detail: $"'{other.DisplayName}' is this tenant's seller of record; a tenant has exactly one.");
        }

        try
        {
            var now = timeProvider.GetUtcNow();
            var profile = entity.DesignateAsSeller(now);
            db.Entry(profile).State = EntityState.Added; // client-generated PK via loaded nav — force INSERT (see AddIdentifier)
            await LegalEntityEvents.PublishIfSellerAsync(publish, entity, now, cancellationToken);
            await audit.RecordAsync(user.Mutation(
                entity.TenantId, "Entity", entity.Id.ToString(), "entity.seller.designate", entity.DisplayName), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.Ok(EntityEndpoints.ToDetail(entity));
        }
        catch (DomainRuleException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [ex.Field ?? "entity"] = [ex.Message] });
        }
    }

    private static async Task<Results<Ok<EntityDetailResponse>, NotFound, ValidationProblem, ProblemHttpResult>> AddEstablishment(
        Guid id,
        AddEstablishmentRequest request,
        EntityDbContext db,
        IPublishEndpoint publish,
        AuditRecorder audit,
        ClaimsPrincipal user,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var entity = await EntityEndpoints.LoadWithDetailsAsync(db.Entities, id, cancellationToken);
        if (entity is null)
        {
            return TypedResults.NotFound();
        }

        if (!entity.IsSeller)
        {
            return NotSeller();
        }

        try
        {
            var now = timeProvider.GetUtcNow();
            var establishment = entity.AddEstablishment(request.Key, request.ToDetails(), now);
            db.Entry(establishment).State = EntityState.Added; // client-generated PK via loaded nav — force INSERT
            await LegalEntityEvents.PublishIfSellerAsync(publish, entity, now, cancellationToken);
            await audit.RecordAsync(user.Mutation(
                entity.TenantId, "Entity", entity.Id.ToString(), "entity.establishment.add", $"{establishment.Key} ({establishment.Jurisdiction})"), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.Ok(EntityEndpoints.ToDetail(entity));
        }
        catch (DomainRuleException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [ex.Field ?? nameof(request.Key)] = [ex.Message] });
        }
    }

    private static async Task<Results<Ok<EntityDetailResponse>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateEstablishment(
        Guid id,
        Guid establishmentId,
        UpdateEstablishmentRequest request,
        EntityDbContext db,
        IPublishEndpoint publish,
        AuditRecorder audit,
        ClaimsPrincipal user,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var entity = await EntityEndpoints.LoadWithDetailsAsync(db.Entities, id, cancellationToken);
        if (entity is null || entity.Establishments.All(e => e.Id != establishmentId))
        {
            return TypedResults.NotFound();
        }

        if (!entity.IsSeller)
        {
            return NotSeller();
        }

        try
        {
            var now = timeProvider.GetUtcNow();
            var establishment = entity.UpdateEstablishment(establishmentId, request.ToDetails(), now);
            await LegalEntityEvents.PublishIfSellerAsync(publish, entity, now, cancellationToken);
            await audit.RecordAsync(user.Mutation(
                entity.TenantId, "Entity", entity.Id.ToString(), "entity.establishment.update", $"{establishment.Key} ({establishment.Jurisdiction})"), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.Ok(EntityEndpoints.ToDetail(entity));
        }
        catch (DomainRuleException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [ex.Field ?? nameof(request.Label)] = [ex.Message] });
        }
    }

    private static ProblemHttpResult NotSeller() => TypedResults.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "The entity is not the seller of record.",
        detail: "Only the tenant's seller of record has establishments — designate the entity as seller first (POST /entities/{id}/seller).");

    private static Task<EntityRecord?> FindSellerAsync(IQueryable<EntityRecord> entities, Guid tenantId, CancellationToken cancellationToken) =>
        entities
            .Include(e => e.Profiles)
            .Include(e => e.Identifiers)
            .Include(e => e.Addresses)
            .Include(e => e.Establishments)
            .Where(e => e.TenantId == tenantId && e.Status == EntityRecordStatus.Active)
            .Where(e => e.Profiles.Any(p => p.Role == EntityRoleKind.TenantOwner && p.Status == EntityProfileStatus.Active))
            .OrderBy(e => e.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    private static SellerIdentityResponse ToSellerResponse(EntityRecord e)
    {
        var office = e.Addresses
            .Where(a => a.IsCurrent && a.Purpose == EntityAddressPurpose.RegisteredOffice)
            .OrderByDescending(a => a.Version)
            .FirstOrDefault();
        return new SellerIdentityResponse(
            e.Id, e.TenantId, e.LegalName, e.TradingName, e.DisplayName,
            [.. e.Identifiers.OrderBy(i => i.Type).Select(EntityEndpoints.ToIdentifierResponse)],
            [.. e.Establishments.OrderBy(s => s.Key).Select(ToEstablishmentResponse)],
            office is null ? null : new EntityAddressResponse(office.Id, office.Purpose, office.Line1, office.Line2, office.City, office.Region, office.Postcode, office.CountryCode));
    }

    internal static SellerEstablishmentResponse ToEstablishmentResponse(SellerEstablishment s) => new(
        s.Id, s.Key, s.Label, s.Line1, s.Line2, s.City, s.Region, s.Postcode, s.CountryCode, s.Jurisdiction, s.UpdatedAt);
}

public sealed record AddEstablishmentRequest(
    [property: Required, StringLength(40, MinimumLength = 2)] string Key,
    [property: Required, StringLength(120, MinimumLength = 2)] string Label,
    [property: Required, StringLength(200, MinimumLength = 2)] string Line1,
    [property: StringLength(200)] string? Line2,
    [property: Required, StringLength(120, MinimumLength = 2)] string City,
    [property: StringLength(120)] string? Region,
    [property: Required, StringLength(32, MinimumLength = 2)] string Postcode,
    [property: Required, StringLength(2, MinimumLength = 2)] string CountryCode,
    // ISO country + optional region (US-CA); omit → the address country.
    [property: StringLength(6)] string? Jurisdiction = null)
{
    internal SellerEstablishmentDetails ToDetails() => new(Label, Line1, Line2, City, Region, Postcode, CountryCode, Jurisdiction);
}

public sealed record UpdateEstablishmentRequest(
    [property: Required, StringLength(120, MinimumLength = 2)] string Label,
    [property: Required, StringLength(200, MinimumLength = 2)] string Line1,
    [property: StringLength(200)] string? Line2,
    [property: Required, StringLength(120, MinimumLength = 2)] string City,
    [property: StringLength(120)] string? Region,
    [property: Required, StringLength(32, MinimumLength = 2)] string Postcode,
    [property: Required, StringLength(2, MinimumLength = 2)] string CountryCode,
    [property: StringLength(6)] string? Jurisdiction = null)
{
    internal SellerEstablishmentDetails ToDetails() => new(Label, Line1, Line2, City, Region, Postcode, CountryCode, Jurisdiction);
}

public sealed record SellerEstablishmentResponse(
    Guid Id,
    string Key,
    string Label,
    string Line1,
    string? Line2,
    string City,
    string? Region,
    string Postcode,
    string CountryCode,
    string Jurisdiction,
    DateTimeOffset UpdatedAt);

public sealed record SellerIdentityResponse(
    Guid EntityId,
    Guid TenantId,
    string LegalName,
    string? TradingName,
    string DisplayName,
    IReadOnlyList<EntityIdentifierResponse> Identifiers,
    IReadOnlyList<SellerEstablishmentResponse> Establishments,
    EntityAddressResponse? RegisteredOffice);
