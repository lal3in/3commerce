using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Catalog.Domain;
using ThreeCommerce.Catalog.Infrastructure;

namespace ThreeCommerce.Catalog.Api.Endpoints;

/// <summary>
/// Offers (product supply profiles, ADR-0028): the multi-supplier home for how a product/variant
/// is sourced + priced. Admin-managed; checkout/publication later select an offer per line.
/// </summary>
public static class OfferEndpoints
{
    public static IEndpointRouteBuilder MapOffers(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/offers").WithTags("Offers")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);

        group.MapGet("/", List);
        group.MapPost("/", Create);
        group.MapPut("/{id:guid}", Update);
        return app;
    }

    private static async Task<Ok<List<OfferDto>>> List(
        Guid? tenantId, Guid? productId, CatalogDbContext db, IConfiguration config, CancellationToken ct)
    {
        var tid = tenantId ?? DefaultTenantId(config);
        var query = db.Offers.AsNoTracking().Where(o => o.TenantId == tid);
        if (productId is { } pid)
        {
            query = query.Where(o => o.ProductId == pid);
        }

        var offers = await query.OrderBy(o => o.Priority).ToListAsync(ct);
        return TypedResults.Ok(offers.Select(ToDto).ToList());
    }

    private static async Task<Results<Created<OfferDto>, BadRequest<string>>> Create(
        CreateOfferRequest request, CatalogDbContext db, IConfiguration config, TimeProvider clock, CancellationToken ct)
    {
        try
        {
            var offer = Offer.Create(
                request.TenantId ?? DefaultTenantId(config), request.ProductId, request.VariantId, request.SupplierId,
                request.SupplyCategory, request.FulfilmentType, request.PriceMinor, request.Currency,
                request.Priority, clock.GetUtcNow());
            db.Offers.Add(offer);
            await db.SaveChangesAsync(ct);
            return TypedResults.Created($"/admin/offers/{offer.Id}", ToDto(offer));
        }
        catch (CatalogRuleException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private static async Task<Results<Ok<OfferDto>, NotFound, BadRequest<string>>> Update(
        Guid id, UpdateOfferRequest request, CatalogDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var offer = await db.Offers.SingleOrDefaultAsync(o => o.Id == id, ct);
        if (offer is null)
        {
            return TypedResults.NotFound();
        }

        try
        {
            var now = clock.GetUtcNow();
            if (request.PriceMinor is { } price)
            {
                offer.SetPrice(price, now);
            }

            if (request.Priority is { } priority)
            {
                offer.SetPriority(priority, now);
            }

            if (request.Active is { } active)
            {
                if (active)
                {
                    offer.Activate(now);
                }
                else
                {
                    offer.Deactivate(now);
                }
            }

            await db.SaveChangesAsync(ct);
            return TypedResults.Ok(ToDto(offer));
        }
        catch (CatalogRuleException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private static Guid DefaultTenantId(IConfiguration config) =>
        Guid.TryParse(config["Tenancy:DefaultTenantId"], out var tenantId)
            ? tenantId
            : new Guid("00000000-0000-0000-0000-000000000001");

    private static OfferDto ToDto(Offer o) =>
        new(o.Id, o.TenantId, o.ProductId, o.VariantId, o.SupplierId, o.SupplyCategory.ToString(),
            o.FulfilmentType.ToString(), o.PricingModel.ToString(), o.PriceMinor, o.Currency, o.Priority, o.Status.ToString());
}

public record CreateOfferRequest(
    Guid? TenantId,
    [property: Required] Guid ProductId,
    Guid? VariantId,
    [property: Required] Guid SupplierId,
    SupplyCategory SupplyCategory,
    FulfilmentType FulfilmentType,
    [property: Range(0, long.MaxValue)] long PriceMinor,
    [property: Required, StringLength(3, MinimumLength = 3)] string Currency,
    int Priority);

public record UpdateOfferRequest(long? PriceMinor, int? Priority, bool? Active);

public record OfferDto(
    Guid Id, Guid TenantId, Guid ProductId, Guid? VariantId, Guid SupplierId, string SupplyCategory,
    string FulfilmentType, string PricingModel, long PriceMinor, string Currency, int Priority, string Status);
