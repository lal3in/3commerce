using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using MassTransit;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;
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
        var ids = offers.Select(o => o.ProductId).Distinct().ToList();
        var titles = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Title, ct);
        return TypedResults.Ok(offers.Select(o => ToDto(o, titles.GetValueOrDefault(o.ProductId, string.Empty))).ToList());
    }

    private static async Task<Results<Created<OfferDto>, BadRequest<string>>> Create(
        CreateOfferRequest request, CatalogDbContext db, IConfiguration config, TimeProvider clock,
        IPublishEndpoint publisher, IAuditRecorder audit, ClaimsPrincipal user, CancellationToken ct)
    {
        try
        {
            var now = clock.GetUtcNow();
            var offer = Offer.Create(
                request.TenantId ?? DefaultTenantId(config), request.ProductId, request.VariantId, request.SupplierId,
                request.SupplyCategory, request.FulfilmentType, request.PriceMinor, request.Currency,
                request.Priority, now);
            offer.SetPricing(request.PricingModel, request.BillingPeriod, ToTiers(request.Tiers), now);
            offer.SetSupplierCost(request.SupplierCostMinor, now);
            db.Offers.Add(offer);
            await audit.RecordAsync(user.Mutation(
                offer.TenantId, "Offer", offer.Id.ToString(), "catalog.offer.create", offer.ProductId.ToString()), ct);
            // Publish BEFORE Save so the OfferChanged outbox row commits in the same transaction and
            // is actually delivered. Publishing after SaveChanges strands it in the change tracker
            // (never flushed) — Ordering's OfferCopy projection then never fires, so subscription/usage
            // offers silently degrade to OneTime/Once at checkout (same outbox trap as the RMA/availability paths).
            await publisher.Publish(ToEvent(offer, await ProductTypeAsync(db, offer.ProductId, ct)), ct);
            await db.SaveChangesAsync(ct);
            return TypedResults.Created($"/admin/offers/{offer.Id}", ToDto(offer));
        }
        catch (CatalogRuleException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private static async Task<Results<Ok<OfferDto>, NotFound, BadRequest<string>>> Update(
        Guid id, UpdateOfferRequest request, CatalogDbContext db, TimeProvider clock,
        IPublishEndpoint publisher, IAuditRecorder audit, ClaimsPrincipal user, CancellationToken ct)
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

            if (request.SupplierCostMinor is { } supplierCost)
            {
                offer.SetSupplierCost(supplierCost, now);
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

            if (request.PricingModel is { } model)
            {
                offer.SetPricing(model, request.BillingPeriod ?? offer.BillingPeriod, ToTiers(request.Tiers), now);
                // SetPricing creates fresh client-keyed tiers on the TRACKED offer's nav; DetectChanges
                // would infer them Modified (UPDATE → 0 rows → DbUpdateConcurrencyException). Tiers aren't
                // loaded here, so every tier in the collection is new — add them through the context.
                db.AddRange(offer.PriceTiers);
            }

            await audit.RecordAsync(user.Mutation(
                offer.TenantId, "Offer", offer.Id.ToString(), "catalog.offer.update", offer.ProductId.ToString()), ct);
            // Publish before Save (see Create) so the OfferChanged outbox row is committed and delivered.
            await publisher.Publish(ToEvent(offer, await ProductTypeAsync(db, offer.ProductId, ct)), ct);
            await db.SaveChangesAsync(ct);
            return TypedResults.Ok(ToDto(offer));
        }
        catch (CatalogRuleException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private static IReadOnlyList<(int FromQuantity, long UnitPriceMinor)> ToTiers(List<PriceTierDto>? tiers) =>
        tiers is null ? [] : tiers.Select(t => (t.FromQuantity, t.UnitPriceMinor)).ToList();

    private static OfferChanged ToEvent(Offer o, ProductType productType) =>
        new(o.Id, o.TenantId, o.ProductId, o.VariantId, o.SupplierId, o.SupplyCategory, o.FulfilmentType, o.PricingModel, o.BillingPeriod, o.Priority, o.IsActive, o.SupplierCostMinor, o.Currency, productType);

    // The product's nature drives the checkout shipping policy (projected onto OfferCopy). Returns the
    // default 0 ("unknown") only if the product row can't be found — an offer always references a real
    // product, so that is a defensive fallback and checkout treats it as a fulfilment-type decision.
    private static async Task<ProductType> ProductTypeAsync(CatalogDbContext db, Guid productId, CancellationToken ct) =>
        await db.Products.AsNoTracking().Where(p => p.Id == productId).Select(p => p.ProductType).FirstOrDefaultAsync(ct);

    private static Guid DefaultTenantId(IConfiguration config) =>
        Guid.TryParse(config["Tenancy:DefaultTenantId"], out var tenantId)
            ? tenantId
            : new Guid("00000000-0000-0000-0000-000000000001");

    private static OfferDto ToDto(Offer o, string productTitle = "") =>
        new(o.Id, o.TenantId, o.ProductId, o.VariantId, o.SupplierId, o.SupplyCategory.ToString(),
            o.FulfilmentType.ToString(), o.PricingModel.ToString(), o.BillingPeriod.ToString(), o.PriceMinor, o.Currency, o.Priority, o.Status.ToString(),
            o.PriceTiers.OrderBy(t => t.FromQuantity).Select(t => new PriceTierDto(t.FromQuantity, t.UnitPriceMinor)).ToList(),
            o.SupplierCostMinor, productTitle);
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
    int Priority,
    PricingModel PricingModel = PricingModel.OneTime,
    BillingPeriod BillingPeriod = BillingPeriod.Once,
    List<PriceTierDto>? Tiers = null,
    [property: Range(0, long.MaxValue)] long SupplierCostMinor = 0);

public record UpdateOfferRequest(
    long? PriceMinor, int? Priority, bool? Active, PricingModel? PricingModel = null, BillingPeriod? BillingPeriod = null, List<PriceTierDto>? Tiers = null,
    [property: Range(0, long.MaxValue)] long? SupplierCostMinor = null);

public record PriceTierDto(int FromQuantity, long UnitPriceMinor);

public record OfferDto(
    Guid Id, Guid TenantId, Guid ProductId, Guid? VariantId, Guid SupplierId, string SupplyCategory,
    string FulfilmentType, string PricingModel, string BillingPeriod, long PriceMinor, string Currency, int Priority, string Status,
    List<PriceTierDto> Tiers, long SupplierCostMinor, string ProductTitle);
