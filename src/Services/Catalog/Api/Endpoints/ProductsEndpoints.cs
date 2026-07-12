using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
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
        string? q,
        string? category,
        string? attrs,
        string? currency = null,
        int? type = null,
        int page = 1,
        int pageSize = 24,
        CancellationToken cancellationToken = default)
    {
        var filters = ParseAttributeFilters(attrs);
        // type is the numeric ProductType (enums cross HTTP as numbers); ignore unknown values.
        var productType = type is { } t && Enum.IsDefined((ProductType)t) ? (ProductType?)t : null;
        var result = await search.SearchAsync(
            new SearchQuery(q, category, filters, page, pageSize, currency, productType), cancellationToken);

        httpContext.Response.Headers["X-Total-Count"] = result.TotalCount.ToString();
        return TypedResults.Ok(result.Hits.ToList());
    }

    private static async Task<Results<Ok<ProductDetailResponse>, NotFound>> GetBySlug(
        string slug, CatalogDbContext db, string? currency, CancellationToken cancellationToken)
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

        var cur = string.IsNullOrWhiteSpace(currency) ? null : currency.Trim().ToUpperInvariant();
        var variants = product.Variants
            .Select(v => (v, price: VariantPriceIn(v, cur)))
            .Where(x => x.price is not null)
            .Select(x => new VariantResponse(x.v.Id, x.v.Sku, x.price!.Value, cur ?? x.v.Currency, x.v.StockQuantity > 0))
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
