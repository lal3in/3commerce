using System.ComponentModel.DataAnnotations;
using MassTransit;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Catalog.Domain;
using ThreeCommerce.Catalog.Infrastructure;

namespace ThreeCommerce.Catalog.Api.Endpoints;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdmin(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin")
            .WithTags("Admin")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);

        group.MapGet("/import-runs", ListImportRuns);
        group.MapPost("/import-runs", TriggerImport);

        // FR-12 catalog editor (BL-2): full create/edit over variants, stock, images, attributes.
        group.MapGet("/products", ListProducts);
        group.MapGet("/products/{id:guid}", GetProduct);
        group.MapPost("/products", CreateProduct);
        group.MapPut("/products/{id:guid}", UpdateProduct);
        group.MapDelete("/products/{id:guid}", DeleteProduct);

        return app;
    }

    private static async Task<Ok<List<ImportRunResponse>>> ListImportRuns(
        CatalogDbContext db, CancellationToken cancellationToken)
    {
        var runs = await db.ImportRuns.AsNoTracking()
            .OrderByDescending(r => r.StartedAt)
            .Take(50)
            .Select(r => new ImportRunResponse(
                r.Id, r.Importer, r.StartedAt, r.CompletedAt, r.RowsRead, r.Accepted, r.Rejected, r.SampleRejections))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(runs);
    }

    /// <summary>
    /// Synchronous in v1 (seconds-to-minutes for 10k rows) — acceptable for an
    /// admin-only endpoint; goes async behind the bus when real feeds arrive.
    /// </summary>
    private static async Task<Created<ImportRunResponse>> TriggerImport(
        ISupplierImporter importer, CancellationToken cancellationToken)
    {
        var result = await importer.RunAsync(cancellationToken);
        return TypedResults.Created($"/admin/import-runs/{result.RunId}", new ImportRunResponse(
            result.RunId, importer.Name, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            result.RowsRead, result.Accepted, result.Rejected, result.SampleRejections.ToList()));
    }

    private static async Task<Ok<List<ProductListItem>>> ListProducts(
        CatalogDbContext db, string? q, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        var query = db.Products.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(p => EF.Functions.ILike(p.Title, $"%{term}%") || EF.Functions.ILike(p.Slug, $"%{term}%"));
        }

        var items = await query
            .OrderBy(p => p.Title)
            .Skip((Math.Max(page, 1) - 1) * pageSize)
            .Take(Math.Clamp(pageSize, 1, 200))
            .Select(p => new ProductListItem(
                p.Id,
                p.Slug,
                p.Title,
                p.Brand,
                p.Variants.Count,
                p.Variants.Count > 0 ? p.Variants.Min(v => v.PriceMinor) : 0,
                p.Variants.Sum(v => v.StockQuantity)))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(items);
    }

    private static async Task<Results<Ok<ProductEditorDto>, NotFound>> GetProduct(
        Guid id, CatalogDbContext db, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking().Include(p => p.Variants)
            .SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        return product is null ? TypedResults.NotFound() : TypedResults.Ok(ToEditorDto(product));
    }

    private static async Task<Results<Created<ProductEditorDto>, Conflict<string>, ValidationProblem>> CreateProduct(
        ProductWriteRequest request, CatalogDbContext db, IPublishEndpoint publisher,
        IConfiguration config, TimeProvider time, CancellationToken cancellationToken)
    {
        if (Validate(request) is { } problem)
        {
            return problem;
        }

        var tenantId = request.TenantId ?? DefaultTenantId(config);
        if (await db.Products.AnyAsync(p => p.TenantId == tenantId && p.Slug == request.Slug, cancellationToken))
        {
            return TypedResults.Conflict($"A product with slug '{request.Slug}' already exists.");
        }

        if (!await db.Categories.AnyAsync(c => c.Id == request.CategoryId && c.TenantId == tenantId, cancellationToken))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.CategoryId)] = ["A valid category is required (products without one are not searchable)."],
            });
        }

        var defaultCurrency = config["Store:Currency"] ?? "EUR";
        var now = time.GetUtcNow();
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Slug = request.Slug,
            Title = request.Title,
            Brand = request.Brand,
            Description = request.Description ?? string.Empty,
            CategoryId = request.CategoryId,
            Attributes = request.Attributes ?? [],
            ImageUrls = request.ImageUrls ?? [],
            CreatedAt = now,
            UpdatedAt = now,
        };
        foreach (var v in request.Variants)
        {
            product.Variants.Add(new Variant
            {
                Id = Guid.CreateVersion7(),
                ProductId = product.Id,
                Sku = v.Sku,
                PriceMinor = v.PriceMinor,
                Currency = string.IsNullOrWhiteSpace(v.Currency) ? defaultCurrency : v.Currency!,
                StockQuantity = v.StockQuantity,
            });
        }

        db.Products.Add(product);
        await PublishUpsertedAsync(publisher, product, defaultCurrency, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/admin/products/{product.Id}", ToEditorDto(product));
    }

    private static async Task<Results<Ok<ProductEditorDto>, NotFound, Conflict<string>, ValidationProblem>> UpdateProduct(
        Guid id, ProductWriteRequest request, CatalogDbContext db, IPublishEndpoint publisher,
        IConfiguration config, TimeProvider time, CancellationToken cancellationToken)
    {
        if (Validate(request) is { } problem)
        {
            return problem;
        }

        var product = await db.Products.Include(p => p.Variants)
            .SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (product is null)
        {
            return TypedResults.NotFound();
        }

        var tenantId = request.TenantId ?? product.TenantId;
        if (await db.Products.AnyAsync(p => p.TenantId == tenantId && p.Slug == request.Slug && p.Id != id, cancellationToken))
        {
            return TypedResults.Conflict($"A product with slug '{request.Slug}' already exists.");
        }

        if (!await db.Categories.AnyAsync(c => c.Id == request.CategoryId && c.TenantId == tenantId, cancellationToken))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.CategoryId)] = ["A valid category is required (products without one are not searchable)."],
            });
        }

        var defaultCurrency = config["Store:Currency"] ?? "EUR";
        product.TenantId = tenantId;
        product.Slug = request.Slug;
        product.Title = request.Title;
        product.Brand = request.Brand;
        product.Description = request.Description ?? string.Empty;
        product.CategoryId = request.CategoryId;
        product.Attributes = request.Attributes ?? [];
        product.ImageUrls = request.ImageUrls ?? [];
        product.UpdatedAt = time.GetUtcNow();

        // Reconcile variants: update matched, add new (Id null/empty), remove the rest.
        var keptIds = new HashSet<Guid>();
        foreach (var v in request.Variants)
        {
            var existing = v.Id is { } vid && vid != Guid.Empty
                ? product.Variants.FirstOrDefault(x => x.Id == vid)
                : null;
            if (existing is null)
            {
                existing = new Variant { Id = Guid.CreateVersion7(), ProductId = product.Id, Sku = v.Sku, Currency = defaultCurrency };
                product.Variants.Add(existing);
            }

            existing.Sku = v.Sku;
            existing.PriceMinor = v.PriceMinor;
            existing.Currency = string.IsNullOrWhiteSpace(v.Currency) ? defaultCurrency : v.Currency!;
            existing.StockQuantity = v.StockQuantity;
            keptIds.Add(existing.Id);
        }

        var removed = product.Variants.Where(v => !keptIds.Contains(v.Id)).ToList();
        foreach (var r in removed)
        {
            product.Variants.Remove(r);
            db.Remove(r);
        }

        await PublishUpsertedAsync(publisher, product, defaultCurrency, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(ToEditorDto(product));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteProduct(
        Guid id, CatalogDbContext db, CancellationToken cancellationToken)
    {
        var product = await db.Products.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (product is null)
        {
            return TypedResults.NotFound();
        }

        db.Products.Remove(product);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static Task PublishUpsertedAsync(IPublishEndpoint publisher, Product product, string fallbackCurrency, CancellationToken ct) =>
        publisher.Publish(new ProductUpserted(
            product.Id,
            product.Slug,
            product.Title,
            product.Variants.Count > 0 ? product.Variants.Min(v => v.PriceMinor) : 0,
            product.Variants.FirstOrDefault()?.Currency ?? fallbackCurrency,
            product.ImageUrls.FirstOrDefault()), ct);

    private static ValidationProblem? Validate(ProductWriteRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Slug)) errors[nameof(request.Slug)] = ["Slug is required."];
        if (string.IsNullOrWhiteSpace(request.Title)) errors[nameof(request.Title)] = ["Title is required."];
        if (string.IsNullOrWhiteSpace(request.Brand)) errors[nameof(request.Brand)] = ["Brand is required."];
        if (request.Variants is null || request.Variants.Count == 0)
            errors[nameof(request.Variants)] = ["At least one variant is required."];
        else
        {
            for (var i = 0; i < request.Variants.Count; i++)
            {
                var v = request.Variants[i];
                if (string.IsNullOrWhiteSpace(v.Sku)) errors[$"Variants[{i}].Sku"] = ["SKU is required."];
                if (v.PriceMinor < 0) errors[$"Variants[{i}].PriceMinor"] = ["Price cannot be negative."];
                if (v.StockQuantity < 0) errors[$"Variants[{i}].StockQuantity"] = ["Stock cannot be negative."];
            }
        }

        return errors.Count > 0 ? TypedResults.ValidationProblem(errors) : null;
    }

    private static Guid DefaultTenantId(IConfiguration config) =>
        Guid.TryParse(config["Tenancy:DefaultTenantId"], out var tenantId)
            ? tenantId
            : Guid.Parse("00000000-0000-0000-0000-000000000001");

    private static ProductEditorDto ToEditorDto(Product p) => new(
        p.Id, p.TenantId, p.Slug, p.Title, p.Brand, p.Description, p.CategoryId, p.Attributes, p.ImageUrls,
        p.Variants.Select(v => new VariantEditorDto(v.Id, v.Sku, v.PriceMinor, v.Currency, v.StockQuantity)).ToList());
}

public record ImportRunResponse(
    Guid Id,
    string Importer,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    int RowsRead,
    int Accepted,
    int Rejected,
    IReadOnlyList<string> SampleRejections);

public record ProductListItem(
    Guid Id, string Slug, string Title, string Brand, int VariantCount, long MinPriceMinor, int TotalStock);

public record ProductEditorDto(
    Guid Id, Guid TenantId, string Slug, string Title, string Brand, string Description, Guid CategoryId,
    Dictionary<string, string> Attributes, List<string> ImageUrls, List<VariantEditorDto> Variants);

public record VariantEditorDto(Guid Id, string Sku, long PriceMinor, string Currency, int StockQuantity);

public record ProductWriteRequest(
    Guid? TenantId, string Slug, string Title, string Brand, string? Description, Guid CategoryId,
    Dictionary<string, string>? Attributes, List<string>? ImageUrls, List<VariantWriteDto> Variants);

public record VariantWriteDto(Guid? Id, string Sku, long PriceMinor, string? Currency, int StockQuantity);
