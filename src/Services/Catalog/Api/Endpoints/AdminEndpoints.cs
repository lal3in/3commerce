using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using MassTransit;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;
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

        // Tenant-scoped catalog feature switches (mandatory per-country ship rules).
        group.MapGet("/settings", GetSettings);
        group.MapPut("/settings", UpdateSettings);

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
                p.Variants.Sum(v => v.StockQuantity),
                p.ImageUrls.FirstOrDefault(),
                p.ProductType == 0 ? ProductType.Physical : p.ProductType))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(items);
    }

    private static async Task<Results<Ok<ProductEditorDto>, NotFound>> GetProduct(
        Guid id, CatalogDbContext db, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking().Include(p => p.Variants).ThenInclude(v => v.Prices)
            .SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        return product is null ? TypedResults.NotFound() : TypedResults.Ok(ToEditorDto(product));
    }

    private static async Task<Results<Created<ProductEditorDto>, Conflict<string>, ValidationProblem>> CreateProduct(
        ProductWriteRequest request, CatalogDbContext db, IPublishEndpoint publisher, IAuditRecorder audit, ClaimsPrincipal user,
        IConfiguration config, TimeProvider time, CancellationToken cancellationToken)
    {
        if (Validate(request) is { } problem)
        {
            return problem;
        }

        var tenantId = request.TenantId ?? DefaultTenantId(config);
        if (await RequireShipRulesProblem(db, tenantId, request, cancellationToken) is { } shipRuleProblem)
        {
            return shipRuleProblem;
        }

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
            Status = request.Status ?? ProductStatus.Active,
            ProductType = request.ProductType ?? ProductType.Physical,
            CreatedAt = now,
            UpdatedAt = now,
        };
        foreach (var v in request.Variants)
        {
            var variant = new Variant
            {
                Id = Guid.CreateVersion7(),
                ProductId = product.Id,
                Sku = v.Sku,
                PriceMinor = v.PriceMinor,
                Currency = string.IsNullOrWhiteSpace(v.Currency) ? defaultCurrency : v.Currency!,
                StockQuantity = v.StockQuantity,
                WeightGrams = v.WeightGrams,
                LengthMm = v.LengthMm,
                WidthMm = v.WidthMm,
                HeightMm = v.HeightMm,
            };
            foreach (var (cur, price) in NormalizePrices(v.Prices))
            {
                variant.Prices.Add(new VariantPrice { Id = Guid.CreateVersion7(), VariantId = variant.Id, Currency = cur, PriceMinor = price });
            }

            product.Variants.Add(variant);
        }

        product.SetShipRules(request.ShipRules?.Select(r => new ProductShipRule(r.CountryCode, r.ChargeDestinationTax, r.ShippingCovered)), now);

        db.Products.Add(product);
        await PublishUpsertedAsync(publisher, product, defaultCurrency, cancellationToken);
        await audit.RecordAsync(user.Mutation(
            product.TenantId, "Product", product.Id.ToString(), "catalog.product.create", product.Title), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/admin/products/{product.Id}", ToEditorDto(product));
    }

    private static async Task<Results<Ok<ProductEditorDto>, NotFound, Conflict<string>, ValidationProblem>> UpdateProduct(
        Guid id, ProductWriteRequest request, CatalogDbContext db, IPublishEndpoint publisher, IAuditRecorder audit, ClaimsPrincipal user,
        IConfiguration config, TimeProvider time, CancellationToken cancellationToken)
    {
        if (Validate(request) is { } problem)
        {
            return problem;
        }

        var product = await db.Products.Include(p => p.Variants).ThenInclude(v => v.Prices)
            .SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (product is null)
        {
            return TypedResults.NotFound();
        }

        var tenantId = request.TenantId ?? product.TenantId;
        if (await RequireShipRulesProblem(db, tenantId, request, cancellationToken) is { } shipRuleProblem)
        {
            return shipRuleProblem;
        }

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
        product.Status = request.Status ?? product.Status; // preserve current status when the editor omits it
        product.ProductType = request.ProductType ?? product.ProductType; // preserve type when omitted
        product.UpdatedAt = time.GetUtcNow();
        product.SetShipRules(request.ShipRules?.Select(r => new ProductShipRule(r.CountryCode, r.ChargeDestinationTax, r.ShippingCovered)), product.UpdatedAt);

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
                // A new client-keyed child added via a TRACKED parent's nav is inferred Modified by
                // DetectChanges (UPDATE → 0 rows → DbUpdateConcurrencyException); add it through the
                // context so EF marks it Added.
                db.Variants.Add(existing);
            }

            existing.Sku = v.Sku;
            existing.PriceMinor = v.PriceMinor;
            existing.Currency = string.IsNullOrWhiteSpace(v.Currency) ? defaultCurrency : v.Currency!;
            existing.StockQuantity = v.StockQuantity;
            existing.WeightGrams = v.WeightGrams;
            existing.LengthMm = v.LengthMm;
            existing.WidthMm = v.WidthMm;
            existing.HeightMm = v.HeightMm;

            // Reconcile per-currency prices for this variant.
            var keptCurrencies = new HashSet<string>();
            foreach (var (cur, price) in NormalizePrices(v.Prices))
            {
                var ep = existing.Prices.FirstOrDefault(x => x.Currency == cur);
                if (ep is null)
                {
                    var newPrice = new VariantPrice { Id = Guid.CreateVersion7(), VariantId = existing.Id, Currency = cur, PriceMinor = price };
                    existing.Prices.Add(newPrice);
                    db.VariantPrices.Add(newPrice); // force Added; a tracked-parent nav Add is inferred Modified
                }
                else
                {
                    ep.PriceMinor = price;
                }

                keptCurrencies.Add(cur);
            }

            foreach (var rp in existing.Prices.Where(x => !keptCurrencies.Contains(x.Currency)).ToList())
            {
                existing.Prices.Remove(rp);
                db.Remove(rp);
            }

            keptIds.Add(existing.Id);
        }

        var removed = product.Variants.Where(v => !keptIds.Contains(v.Id)).ToList();
        foreach (var r in removed)
        {
            product.Variants.Remove(r);
            db.Remove(r);
        }

        await PublishUpsertedAsync(publisher, product, defaultCurrency, cancellationToken);
        await audit.RecordAsync(user.Mutation(
            product.TenantId, "Product", product.Id.ToString(), "catalog.product.update", product.Title), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(ToEditorDto(product));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteProduct(
        Guid id, CatalogDbContext db, IAuditRecorder audit, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var product = await db.Products.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (product is null)
        {
            return TypedResults.NotFound();
        }

        db.Products.Remove(product);
        await audit.RecordAsync(user.Mutation(
            product.TenantId, "Product", product.Id.ToString(), "catalog.product.delete", product.Title), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<CatalogSettingsResponse>> GetSettings(
        CatalogDbContext db, IConfiguration config, Guid? tenantId, CancellationToken cancellationToken)
    {
        var tenant = tenantId ?? DefaultTenantId(config);
        var settings = await db.TenantCatalogSettings.AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenant, cancellationToken);
        return TypedResults.Ok(new CatalogSettingsResponse(tenant, settings?.RequireProductShipRules ?? false));
    }

    private static async Task<Ok<CatalogSettingsResponse>> UpdateSettings(
        CatalogSettingsRequest request, CatalogDbContext db, IAuditRecorder audit, ClaimsPrincipal user,
        IConfiguration config, TimeProvider time, CancellationToken cancellationToken)
    {
        var tenant = request.TenantId ?? DefaultTenantId(config);
        var settings = await db.TenantCatalogSettings.SingleOrDefaultAsync(s => s.TenantId == tenant, cancellationToken);
        if (settings is null)
        {
            settings = new TenantCatalogSettings { TenantId = tenant };
            db.TenantCatalogSettings.Add(settings);
        }

        settings.RequireProductShipRules = request.RequireProductShipRules;
        settings.UpdatedAt = time.GetUtcNow();
        await audit.RecordAsync(user.Mutation(
            tenant, "CatalogSettings", tenant.ToString(), "catalog.settings.update", $"RequireProductShipRules={request.RequireProductShipRules}"), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new CatalogSettingsResponse(tenant, settings.RequireProductShipRules));
    }

    // When the tenant's mandatory gate is on, a product write must carry at least one ship rule.
    private static async Task<ValidationProblem?> RequireShipRulesProblem(
        CatalogDbContext db, Guid tenantId, ProductWriteRequest request, CancellationToken cancellationToken)
    {
        var settings = await db.TenantCatalogSettings.AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);
        if (settings?.RequireProductShipRules == true && (request.ShipRules is null || request.ShipRules.Count == 0))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["ShipRules"] = ["At least one per-country ship rule is required."],
            });
        }

        return null;
    }

    private static Task PublishUpsertedAsync(IPublishEndpoint publisher, Product product, string fallbackCurrency, CancellationToken ct) =>
        publisher.Publish(new ProductUpserted(
            product.Id,
            product.Slug,
            product.Title,
            product.Variants.Count > 0 ? product.Variants.Min(v => v.PriceMinor) : 0,
            product.Variants.FirstOrDefault()?.Currency ?? fallbackCurrency,
            product.ImageUrls.FirstOrDefault(),
            product.Variants.Select(v => new ProductVariantUpserted(
                v.Id, v.Sku, v.PriceMinor, v.Currency, v.StockQuantity,
                v.Prices.Select(p => new VariantCurrencyPrice(p.Currency, p.PriceMinor)).ToList())).ToList(),
            product.ShipRules.Select(r => new ProductShipRuleContract(r.CountryCode, r.ChargeDestinationTax, r.ShippingCovered)).ToList()), ct);

    // Normalize tenant-entered per-currency prices: 3-letter upper ISO, last write wins per currency, drop blanks.
    private static IEnumerable<(string Currency, long PriceMinor)> NormalizePrices(List<CurrencyPriceDto>? prices)
    {
        if (prices is null)
        {
            yield break;
        }

        var seen = new HashSet<string>();
        foreach (var p in prices)
        {
            var cur = (p.Currency ?? string.Empty).Trim().ToUpperInvariant();
            if (cur.Length == 3 && p.PriceMinor >= 0 && seen.Add(cur))
            {
                yield return (cur, p.PriceMinor);
            }
        }
    }

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
        p.Id, p.TenantId, p.Slug, p.Title, p.Brand, p.Description, p.CategoryId, p.Attributes, p.ImageUrls, p.Status,
        p.Variants.Select(v => new VariantEditorDto(v.Id, v.Sku, v.PriceMinor, v.Currency, v.StockQuantity, v.WeightGrams, v.LengthMm, v.WidthMm, v.HeightMm,
            v.Prices.Select(pr => new CurrencyPriceDto(pr.Currency, pr.PriceMinor)).ToList())).ToList(),
        p.ProductType == 0 ? ProductType.Physical : p.ProductType,
        p.ShipRules.Select(r => new ProductShipRuleDto(r.CountryCode, r.ChargeDestinationTax, r.ShippingCovered)).ToList());
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
    Guid Id, string Slug, string Title, string Brand, int VariantCount, long MinPriceMinor, int TotalStock, string? ImageUrl,
    ProductType ProductType);

public record ProductEditorDto(
    Guid Id, Guid TenantId, string Slug, string Title, string Brand, string Description, Guid CategoryId,
    Dictionary<string, string> Attributes, List<string> ImageUrls, ProductStatus Status, List<VariantEditorDto> Variants,
    ProductType ProductType, List<ProductShipRuleDto> ShipRules);

public record VariantEditorDto(Guid Id, string Sku, long PriceMinor, string Currency, int StockQuantity, int? WeightGrams, int? LengthMm, int? WidthMm, int? HeightMm, List<CurrencyPriceDto> Prices);

// Tenant-authored explicit price per currency (no FX).
public record CurrencyPriceDto(string Currency, long PriceMinor);

// Per-product, per-destination ship rule (ISO-2 country or '*' whole-world default).
public record ProductShipRuleDto(string CountryCode, bool ChargeDestinationTax, bool ShippingCovered);

public record ProductWriteRequest(
    Guid? TenantId, string Slug, string Title, string Brand, string? Description, Guid CategoryId,
    Dictionary<string, string>? Attributes, List<string>? ImageUrls, List<VariantWriteDto> Variants,
    ProductStatus? Status = null, ProductType? ProductType = null, List<ProductShipRuleDto>? ShipRules = null);

// Tenant-scoped catalog feature switches surfaced to the Admin portal.
public record CatalogSettingsResponse(Guid TenantId, bool RequireProductShipRules);

public record CatalogSettingsRequest(Guid? TenantId, bool RequireProductShipRules);

public record VariantWriteDto(Guid? Id, string Sku, long PriceMinor, string? Currency, int StockQuantity, int? WeightGrams = null, int? LengthMm = null, int? WidthMm = null, int? HeightMm = null, List<CurrencyPriceDto>? Prices = null);
