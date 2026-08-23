using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Catalog.Domain;
using ThreeCommerce.Catalog.Infrastructure;

namespace ThreeCommerce.Catalog.Api.Endpoints;

public static class ProductsEndpoints
{
    public static IEndpointRouteBuilder MapProducts(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/products").WithTags("Products");
        group.MapGet("/", Search);
        group.MapGet("/{slug}", GetBySlug);

        app.MapGroup("/categories").WithTags("Categories").MapGet("/", ListCategories);

        return app;
    }

    /// <summary>q = search text; attrs = "color:red,size:m"; X-Total-Count on response.</summary>
    private static async Task<Ok<List<ProductHit>>> Search(
        HttpContext httpContext,
        ISearchProvider search,
        CatalogDbContext db,
        TimeProvider clock,
        string? q,
        string? category,
        string? attrs,
        string? currency = null,
        int? type = null,
        Guid? storefrontId = null,
        int page = 1,
        int pageSize = 24,
        CancellationToken cancellationToken = default)
    {
        var filters = ParseAttributeFilters(attrs);
        // type is the numeric ProductType (enums cross HTTP as numbers); ignore unknown values.
        var productType = type is { } t && Enum.IsDefined((ProductType)t) ? (ProductType?)t : null;
        // storefrontId scopes a public listing to that storefront's PUBLISHED catalog (merchandising).
        var result = await search.SearchAsync(
            new SearchQuery(q, category, filters, page, pageSize, currency, productType, storefrontId), cancellationToken);

        // Offer-as-price on the listing (shown == charged): a PRODUCT-LEVEL offer that is active + in-window
        // for this storefront + currency sets every variant's price, so it sets the listed min price too.
        // (Variant-specific listing overrides are resolved on the detail page.) Applied only when a currency
        // is requested, so single-currency admin/global listings are unaffected.
        var hits = result.Hits.ToList();
        var cur = string.IsNullOrWhiteSpace(currency) ? null : currency.Trim().ToUpperInvariant();
        if (cur is not null && hits.Count > 0)
        {
            var now = clock.GetUtcNow();
            var store = storefrontId ?? Guid.Empty;
            var hitIds = hits.Select(h => h.Id).ToList();
            // All ACTIVE offers for these products in the store's currency (any grain/price): used both to
            // gate availability on supplier approval (DECISION A) and to apply the product-level offer price.
            var offers = await db.Offers.AsNoTracking()
                .Where(o => hitIds.Contains(o.ProductId) && o.Currency == cur && o.Status == OfferStatus.Active)
                .ToListAsync(cancellationToken);
            if (offers.Count > 0)
            {
                var approved = await ApprovedSupplierSetAsync(db, offers, cancellationToken);

                // DECISION A (strict): a product whose covering offers (this store + currency) are ALL from
                // unapproved suppliers is unavailable → hidden from the listing. A product with no covering
                // offer keeps its catalog behaviour (offerless scope guard). "Covering" = active,
                // currency-matching, storefront null-or-match.
                hits = hits.Where(h =>
                {
                    var covering = offers.Where(o => o.ProductId == h.Id
                        && (o.StorefrontId == null || o.StorefrontId == store)).ToList();
                    return covering.Count == 0 || covering.Exists(o => approved.Contains(o.SupplierId));
                }).ToList();

                // Offer-as-price: only an APPROVED, product-level, in-window offer sets the listed min price.
                hits = hits.Select(h =>
                {
                    var offerPrice = offers
                        .Where(o => o.ProductId == h.Id && o.VariantId == null && o.PriceMinor > 0
                            && approved.Contains(o.SupplierId) && o.IsEffectiveAt(now, store))
                        .OrderByDescending(o => o.StorefrontId == store)
                        .ThenBy(o => o.Priority)
                        .Select(o => (long?)o.PriceMinor)
                        .FirstOrDefault();
                    return offerPrice is { } p ? h with { MinPriceMinor = p } : h;
                }).ToList();
            }
        }

        httpContext.Response.Headers["X-Total-Count"] = result.TotalCount.ToString();
        return TypedResults.Ok(hits);
    }

    private static async Task<Results<Ok<ProductDetailResponse>, NotFound>> GetBySlug(
        string slug, CatalogDbContext db, TimeProvider clock, string? currency, Guid? storefrontId, CancellationToken cancellationToken)
    {
        // Public detail gate: an Inactive product is treated as non-existent here (404). Admin
        // GetProduct (by id) stays unfiltered so the catalog editor can still load/edit it.
        var product = await db.Products.AsNoTracking()
            .Include(p => p.Variants).ThenInclude(v => v.Prices)
            .SingleOrDefaultAsync(p => p.Slug == slug && p.Status == ProductStatus.Active, cancellationToken);
        if (product is null)
        {
            return TypedResults.NotFound();
        }

        // Storefront-scoped detail: a product not PUBLISHED to this storefront is 404 here too, so a
        // direct link can't reach an unpublished product (matches the storefront-scoped listing).
        if (storefrontId is { } sid && !await db.Set<ProductPublication>().AsNoTracking().AnyAsync(
            pub => pub.ProductId == product.Id && pub.StorefrontId == sid && pub.State == PublicationState.Published, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var cur = string.IsNullOrWhiteSpace(currency) ? null : currency.Trim().ToUpperInvariant();

        // Offer-as-price (ADR-0028): an active, in-window offer for this storefront + currency SHOWS the
        // price the shopper will be CHARGED (shown == charged). Loaded once and applied per variant below —
        // a variant-specific offer beats a product-level one; a storefront-scoped offer beats an all-store one.
        var now = clock.GetUtcNow();
        var store = storefrontId ?? Guid.Empty;
        // All ACTIVE offers for this product (any grain/price): drive both the approval-availability gate
        // (DECISION A) and offer-as-price. Loaded regardless of price so a zero-price supply offer (e.g. a
        // usage meter) still counts toward coverage.
        var offers = await db.Offers.AsNoTracking()
            .Where(o => o.ProductId == product.Id && o.TenantId == product.TenantId && o.Status == OfferStatus.Active)
            .ToListAsync(cancellationToken);
        var approved = await ApprovedSupplierSetAsync(db, offers, cancellationToken);
        var pricedApprovedOffers = offers.Where(o => o.PriceMinor > 0 && approved.Contains(o.SupplierId)).ToList();
        var variants = product.Variants
            .Select(v => (v, price: VariantPriceIn(v, cur)))
            .Where(x => x.price is not null)
            .Select(x =>
            {
                var lineCurrency = cur ?? x.v.Currency;
                // DECISION A (strict): a variant whose covering offers (this store + currency) are ALL from
                // unapproved suppliers is unavailable (out of stock), regardless of stock. A variant with no
                // covering offer keeps its catalog stock flag (offerless scope guard).
                var covering = offers.Where(o => (o.VariantId == x.v.Id || o.VariantId == null)
                    && string.Equals(o.Currency, lineCurrency, StringComparison.OrdinalIgnoreCase)
                    && (o.StorefrontId == null || o.StorefrontId == store)).ToList();
                var supplierApproved = covering.Count == 0 || covering.Exists(o => approved.Contains(o.SupplierId));
                var offerPrice = EffectiveOfferPrice(pricedApprovedOffers, x.v.Id, store, lineCurrency, now);
                return new VariantResponse(x.v.Id, x.v.Sku, offerPrice ?? x.price!.Value, lineCurrency, supplierApproved && x.v.StockQuantity > 0);
            })
            .ToList();

        // Hidden on a storefront whose currency the tenant priced no variant in.
        if (cur is not null && variants.Count == 0)
        {
            return TypedResults.NotFound();
        }

        var category = await db.Categories.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == product.CategoryId, cancellationToken);

        return TypedResults.Ok(new ProductDetailResponse(
            product.Id,
            product.Slug,
            product.Title,
            product.Brand,
            product.Description,
            category?.Slug,
            category?.Name,
            product.Attributes,
            product.ImageUrls,
            variants,
            product.ProductType == 0 ? ProductType.Physical : product.ProductType));
    }

    // Variant price in the requested currency: VariantPrice override, else base price when base currency
    // matches, else null (not sold in that currency). Null currency = base price (single-currency callers).
    private static long? VariantPriceIn(Variant v, string? currency)
    {
        if (currency is null)
        {
            return v.PriceMinor;
        }

        var match = v.Prices.FirstOrDefault(p => p.Currency == currency);
        if (match is not null)
        {
            return match.PriceMinor;
        }

        return string.Equals(v.Currency, currency, StringComparison.OrdinalIgnoreCase) ? v.PriceMinor : null;
    }

    // The price an effective offer sets for a variant on a storefront (offer-as-price, ADR-0028): among the
    // offers targeting this product that are active + in-window for the storefront and denominated in the
    // line's currency, a variant-specific offer beats a product-level (VariantId null) one, a storefront-scoped
    // offer beats an all-storefront one, then lowest priority wins. Null = no effective offer (keep catalog).
    private static long? EffectiveOfferPrice(
        IReadOnlyList<Offer> offers, Guid variantId, Guid storefrontId, string currency, DateTimeOffset now) =>
        offers
            .Where(o => (o.VariantId == variantId || o.VariantId == null)
                && string.Equals(o.Currency, currency, StringComparison.OrdinalIgnoreCase)
                && o.IsEffectiveAt(now, storefrontId))
            .OrderByDescending(o => o.VariantId == variantId)
            .ThenByDescending(o => o.StorefrontId == storefrontId)
            .ThenBy(o => o.Priority)
            .Select(o => (long?)o.PriceMinor)
            .FirstOrDefault();

    // The approved suppliers among a set of offers' suppliers (DECISION A). An offer whose supplier has no
    // SupplierApprovalCopy row — or a row with Approved false — is excluded everywhere it would be resolved.
    private static async Task<HashSet<Guid>> ApprovedSupplierSetAsync(
        CatalogDbContext db, IEnumerable<Offer> offers, CancellationToken cancellationToken)
    {
        var supplierIds = offers.Select(o => o.SupplierId).Distinct().ToList();
        var approved = await db.SupplierApprovalCopies.AsNoTracking()
            .Where(s => s.Approved && supplierIds.Contains(s.SupplierId))
            .Select(s => s.SupplierId)
            .ToListAsync(cancellationToken);
        return approved.ToHashSet();
    }

    private static async Task<Ok<List<CategoryResponse>>> ListCategories(
        CatalogDbContext db, CancellationToken cancellationToken)
    {
        var categories = await db.Categories.AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new CategoryResponse(c.Id, c.Slug, c.Name, c.ParentId))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(categories);
    }

    private static Dictionary<string, string> ParseAttributeFilters(string? attrs)
    {
        var filters = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(attrs))
        {
            return filters;
        }

        foreach (var pair in attrs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
            {
                filters[parts[0]] = parts[1];
            }
        }

        return filters;
    }
}

public record ProductDetailResponse(
    Guid Id,
    string Slug,
    string Title,
    string Brand,
    string Description,
    string? CategorySlug,
    string? CategoryName,
    Dictionary<string, string> Attributes,
    List<string> ImageUrls,
    List<VariantResponse> Variants,
    ProductType ProductType);

public record VariantResponse(Guid Id, string Sku, long PriceMinor, string Currency, bool InStock);

public record CategoryResponse(Guid Id, string Slug, string Name, Guid? ParentId);
