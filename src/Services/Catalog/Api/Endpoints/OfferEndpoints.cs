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
        Guid? tenantId, string? product, Guid? supplierId, CatalogDbContext db, IConfiguration config, CancellationToken ct)
    {
        var tid = tenantId ?? DefaultTenantId(config);
        var query = db.Offers.AsNoTracking().Where(o => o.TenantId == tid);
        // Product filter: one term matching by product id OR title. A GUID term matches the exact ProductId
        // (the Catalog page's per-product cost table relies on this). Any other non-empty term is a
        // case-insensitive title substring: resolve the tenant's matching product ids and keep offers whose
        // ProductId is one of them. Empty → no product filter. Mirrors the Orders page's id-or-title search,
        // and (unlike a bare Guid? param) no longer 400s on free text at minimal-API model binding.
        if (!string.IsNullOrWhiteSpace(product))
        {
            var term = product.Trim();
            if (Guid.TryParse(term, out var pid))
            {
                query = query.Where(o => o.ProductId == pid);
            }
            else
            {
                var matchingProductIds = db.Products
                    .Where(p => p.TenantId == tid && EF.Functions.ILike(p.Title, $"%{term}%"))
                    .Select(p => p.Id);
                query = query.Where(o => matchingProductIds.Contains(o.ProductId));
            }
        }

        if (supplierId is { } sid)
        {
            query = query.Where(o => o.SupplierId == sid);
        }

        // Priority alone leaves equal-priority offers (the seed sets many to the same priority) in an
        // arbitrary, run-to-run order; a stable secondary key (the Guid v7 id, creation-ordered) makes the
        // Suppliers cost table render the same rows in the same order across reloads.
        var offers = await query.OrderBy(o => o.Priority).ThenBy(o => o.Id).ToListAsync(ct);
        var ids = offers.Select(o => o.ProductId).Distinct().ToList();
        var titles = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Title, ct);
        // Resolve variant SKUs for the referenced variants in one query, so the Suppliers page can label
        // each supplied line by its variant SKU rather than a bare GUID.
        var variantIds = offers.Where(o => o.VariantId is not null).Select(o => o.VariantId!.Value).Distinct().ToList();
        var skus = await db.Variants.AsNoTracking().Where(v => variantIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Sku, ct);
        return TypedResults.Ok(offers.Select(o => ToDto(
            o,
            titles.GetValueOrDefault(o.ProductId, string.Empty),
            o.VariantId is { } vid ? skus.GetValueOrDefault(vid) : null)).ToList());
    }

    private static async Task<Results<Created<OfferDto>, BadRequest<string>>> Create(
        CreateOfferRequest request, CatalogDbContext db, IConfiguration config, TimeProvider clock,
        IPublishEndpoint publisher, IAuditRecorder audit, ClaimsPrincipal user, CancellationToken ct)
    {
        try
        {
            var now = clock.GetUtcNow();
            var tenantId = request.TenantId ?? DefaultTenantId(config);
            // An offer is uniquely identified by (TenantId, ProductId, VariantId, SupplierId, StorefrontId).
            // Different suppliers on one product (multi-supplier) and the same supplier with a different
            // StorefrontId (per-storefront pricing) are legitimately distinct and must still create. Only the
            // EXACT same key repeated is a duplicate — reject it so a non-idempotent caller (e.g. a re-run seed)
            // can't pile many rows onto one key and make the Suppliers cost table list a variant twice.
            var duplicate = await db.Offers.AsNoTracking().AnyAsync(
                o => o.TenantId == tenantId
                    && o.ProductId == request.ProductId
                    && o.VariantId == request.VariantId
                    && o.SupplierId == request.SupplierId
                    && o.StorefrontId == request.StorefrontId,
                ct);
            if (duplicate)
            {
                return TypedResults.BadRequest(
                    "An offer already exists for this tenant, product, variant, supplier and storefront. "
                    + "Update the existing offer instead of creating a duplicate.");
            }

            var offer = Offer.Create(
                tenantId, request.ProductId, request.VariantId, request.SupplierId,
                request.SupplyCategory, request.FulfilmentType, request.PriceMinor, request.Currency,
                request.Priority, now);
            offer.SetPricing(request.PricingModel, request.BillingPeriod, ToTiers(request.Tiers), now);
            offer.SetSupplierCost(request.SupplierCostMinor, now);
            offer.SetStorefront(request.StorefrontId, now);
            offer.SetActiveWindow(request.ActiveFrom, request.ActiveUntil, now);
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

            // Storefront scope + active window are set together, only when the caller opts in (ApplyScope) —
            // any of the three may legitimately be null (all-storefront / open-ended), so a partial update
            // that omits them (e.g. the Suppliers page editing only supplier cost) must not wipe them.
            if (request.ApplyScope)
            {
                offer.SetStorefront(request.StorefrontId, now);
                offer.SetActiveWindow(request.ActiveFrom, request.ActiveUntil, now);
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
        new(o.Id, o.TenantId, o.ProductId, o.VariantId, o.SupplierId, o.SupplyCategory, o.FulfilmentType, o.PricingModel, o.BillingPeriod, o.Priority, o.IsActive, o.SupplierCostMinor, o.Currency, productType,
            o.PriceMinor, o.StorefrontId, o.ActiveFrom, o.ActiveUntil);

    // The product's nature drives the checkout shipping policy (projected onto OfferCopy). Returns the
    // default 0 ("unknown") only if the product row can't be found — an offer always references a real
    // product, so that is a defensive fallback and checkout treats it as a fulfilment-type decision.
    private static async Task<ProductType> ProductTypeAsync(CatalogDbContext db, Guid productId, CancellationToken ct) =>
        await db.Products.AsNoTracking().Where(p => p.Id == productId).Select(p => p.ProductType).FirstOrDefaultAsync(ct);

    private static Guid DefaultTenantId(IConfiguration config) =>
        Guid.TryParse(config["Tenancy:DefaultTenantId"], out var tenantId)
            ? tenantId
            : new Guid("00000000-0000-0000-0000-000000000001");

    private static OfferDto ToDto(Offer o, string productTitle = "", string? variantSku = null) =>
        new(o.Id, o.TenantId, o.ProductId, o.VariantId, o.SupplierId, o.SupplyCategory.ToString(),
            o.FulfilmentType.ToString(), o.PricingModel.ToString(), o.BillingPeriod.ToString(), o.PriceMinor, o.Currency, o.Priority, o.Status.ToString(),
            o.PriceTiers.OrderBy(t => t.FromQuantity).Select(t => new PriceTierDto(t.FromQuantity, t.UnitPriceMinor)).ToList(),
            o.SupplierCostMinor, productTitle, variantSku, o.StorefrontId, o.ActiveFrom, o.ActiveUntil);
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
    [property: Range(0, long.MaxValue)] long SupplierCostMinor = 0,
    // Storefront scope + active window (offer-as-price). StorefrontId null = all storefronts of the currency;
    // the window bounds are open-ended when null.
    Guid? StorefrontId = null,
    DateTimeOffset? ActiveFrom = null,
    DateTimeOffset? ActiveUntil = null);

public record UpdateOfferRequest(
    long? PriceMinor, int? Priority, bool? Active, PricingModel? PricingModel = null, BillingPeriod? BillingPeriod = null, List<PriceTierDto>? Tiers = null,
    [property: Range(0, long.MaxValue)] long? SupplierCostMinor = null,
    // ApplyScope opts the update in to setting StorefrontId + the active window (any of which may be null).
    // A partial update that leaves it false keeps the offer's current scope/window (e.g. the Suppliers page).
    bool ApplyScope = false,
    Guid? StorefrontId = null,
    DateTimeOffset? ActiveFrom = null,
    DateTimeOffset? ActiveUntil = null);

public record PriceTierDto(int FromQuantity, long UnitPriceMinor);

public record OfferDto(
    Guid Id, Guid TenantId, Guid ProductId, Guid? VariantId, Guid SupplierId, string SupplyCategory,
    string FulfilmentType, string PricingModel, string BillingPeriod, long PriceMinor, string Currency, int Priority, string Status,
    List<PriceTierDto> Tiers, long SupplierCostMinor, string ProductTitle, string? VariantSku = null,
    Guid? StorefrontId = null, DateTimeOffset? ActiveFrom = null, DateTimeOffset? ActiveUntil = null);
