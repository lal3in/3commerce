using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Pricing.Domain;
using ThreeCommerce.Pricing.Infrastructure;

namespace ThreeCommerce.Pricing.Api.Endpoints;

/// <summary>Price admin (mt7_1): the dedicated home for product prices + graduated tiers.</summary>
public static class PriceEndpoints
{
    public static IEndpointRouteBuilder MapPrices(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/prices").WithTags("Prices")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);

        group.MapGet("/", List);
        group.MapPost("/", Create);
        group.MapGet("/{id:guid}/quote", Quote);
        return app;
    }

    private static async Task<Ok<List<PriceDto>>> List(
        Guid? tenantId, PricingDbContext db, IConfiguration config, CancellationToken ct)
    {
        var tenant = tenantId ?? DefaultTenantId(config);
        var prices = await db.Prices.Include(p => p.Tiers).AsNoTracking()
            .Where(p => p.TenantId == tenant).OrderByDescending(p => p.CreatedAt).ToListAsync(ct);
        return TypedResults.Ok(prices.Select(ToDto).ToList());
    }

    private static async Task<Results<Created<PriceDto>, BadRequest<string>>> Create(
        CreatePriceRequest request, PricingDbContext db, TimeProvider clock, IConfiguration config, CancellationToken ct)
    {
        try
        {
            var price = Price.Create(
                request.TenantId ?? DefaultTenantId(config), request.ProductId, request.VariantId, request.SupplierId,
                request.AmountMinor, request.Currency, request.PricingModel, request.BillingPeriod,
                (request.Tiers ?? []).Select(t => (t.FromQuantity, t.UnitPriceMinor)).ToList(), clock.GetUtcNow());
            db.Prices.Add(price);
            await db.SaveChangesAsync(ct);
            return TypedResults.Created($"/admin/prices/{price.Id}", ToDto(price));
        }
        catch (PricingRuleException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private static async Task<Results<Ok<QuoteDto>, NotFound>> Quote(
        Guid id, [Range(1, int.MaxValue)] int quantity, PricingDbContext db, CancellationToken ct)
    {
        var price = await db.Prices.Include(p => p.Tiers).AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct);
        return price is null ? TypedResults.NotFound() : TypedResults.Ok(new QuoteDto(quantity, price.PriceFor(quantity), price.Currency));
    }

    private static Guid DefaultTenantId(IConfiguration config) =>
        Guid.TryParse(config["Tenancy:DefaultTenantId"], out var tenantId)
            ? tenantId
            : new Guid("00000000-0000-0000-0000-000000000001");

    private static PriceDto ToDto(Price p) =>
        new(p.Id, p.ProductId, p.VariantId, p.SupplierId, p.PricingModel.ToString(), p.BillingPeriod.ToString(), p.AmountMinor, p.Currency,
            p.Tiers.OrderBy(t => t.FromQuantity).Select(t => new PriceTierDto(t.FromQuantity, t.UnitPriceMinor)).ToList());
}

public record CreatePriceRequest(
    Guid? TenantId, [property: Required] Guid ProductId, Guid? VariantId, Guid? SupplierId,
    [property: Range(0, long.MaxValue)] long AmountMinor, [property: Required, StringLength(3, MinimumLength = 3)] string Currency,
    PricingModel PricingModel = PricingModel.OneTime, BillingPeriod BillingPeriod = BillingPeriod.Once, List<PriceTierDto>? Tiers = null);

public record PriceTierDto(int FromQuantity, long UnitPriceMinor);

public record PriceDto(
    Guid Id, Guid ProductId, Guid? VariantId, Guid? SupplierId, string PricingModel, string BillingPeriod, long AmountMinor, string Currency,
    List<PriceTierDto> Tiers);

public record QuoteDto(int Quantity, long TotalMinor, string Currency);
