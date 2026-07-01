using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Catalog.Domain;
using ThreeCommerce.Catalog.Infrastructure;

namespace ThreeCommerce.Catalog.Api.Endpoints;

public static class StorefrontEndpoints
{
    public static IEndpointRouteBuilder MapStorefrontAdmin(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/storefronts")
            .WithTags("Admin Storefronts")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);

        group.MapGet("/", List);
        group.MapPost("/", Create);
        group.MapPut("/{id:guid}", Update);
        group.MapPost("/{id:guid}/domains", AddDomain);
        group.MapGet("/{id:guid}/readiness", Readiness);
        group.MapPost("/{id:guid}/preview", Preview);
        group.MapPost("/{id:guid}/activate", Activate);
        group.MapPost("/{id:guid}/pause", Pause);
        group.MapPost("/{id:guid}/archive", Archive);
        group.MapPost("/{id:guid}/products", AssignProduct);
        group.MapGet("/{id:guid}/products/{productId:guid}/readiness", ProductReadiness);
        group.MapPost("/{id:guid}/products/{productId:guid}/publish", PublishProduct);
        group.MapPost("/{id:guid}/products/{productId:guid}/unpublish", UnpublishProduct);

        return app;
    }

    private static async Task<Ok<List<StorefrontResponse>>> List(
        Guid tenantId,
        CatalogDbContext db,
        CancellationToken cancellationToken)
    {
        var storefronts = await db.Storefronts.AsNoTracking()
            .Include(s => s.Domains)
            .Where(s => s.TenantId == tenantId && s.State != StorefrontState.Archived)
            .OrderBy(s => s.Name)
            .Select(s => ToResponse(s))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(storefronts);
    }

    private static async Task<Results<Created<StorefrontResponse>, ValidationProblem, Conflict<string>>> Create(
        CreateStorefrontRequest request,
        CatalogDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (await db.Storefronts.AnyAsync(s => s.TenantId == request.TenantId && s.Name == request.Name.Trim(), cancellationToken))
        {
            return TypedResults.Conflict($"Storefront '{request.Name}' already exists for this tenant.");
        }

        try
        {
            var storefront = Storefront.Create(request.TenantId, request.Name, time.GetUtcNow());
            storefront.SetVisibility(request.Visibility, request.AccessPasswordHash, time.GetUtcNow());
            storefront.ConfigureCommerce(request.PublicUrl ?? string.Empty, request.Currency ?? "EUR", request.TaxRegime, request.TaxRateBasisPoints, time.GetUtcNow());
            db.Storefronts.Add(storefront);
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.Created($"/admin/storefronts/{storefront.Id}", ToResponse(storefront));
        }
        catch (CatalogRuleException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Name)] = [ex.Message] });
        }
    }


    private static async Task<Results<Ok<StorefrontResponse>, NotFound, ValidationProblem, Conflict<string>>> Update(
        Guid id,
        UpdateStorefrontRequest request,
        CatalogDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var storefront = await db.Storefronts.Include(s => s.Domains).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (storefront is null)
        {
            return TypedResults.NotFound();
        }

        if (!string.Equals(storefront.Name, request.Name.Trim(), StringComparison.Ordinal) &&
            await db.Storefronts.AnyAsync(s => s.TenantId == storefront.TenantId && s.Name == request.Name.Trim(), cancellationToken))
        {
            return TypedResults.Conflict($"Storefront '{request.Name}' already exists for this tenant.");
        }

        try
        {
            var now = time.GetUtcNow();
            storefront.Rename(request.Name, now);
            storefront.SetVisibility(request.Visibility, request.AccessPasswordHash, now);
            storefront.ConfigureCommerce(request.PublicUrl ?? string.Empty, request.Currency, request.TaxRegime, request.TaxRateBasisPoints, now);
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.Ok(ToResponse(storefront));
        }
        catch (CatalogRuleException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Name)] = [ex.Message] });
        }
    }

    private static async Task<Results<Ok<StorefrontResponse>, NotFound, ValidationProblem>> AddDomain(
        Guid id,
        AddStorefrontDomainRequest request,
        CatalogDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var storefront = await db.Storefronts.Include(s => s.Domains).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (storefront is null)
        {
            return TypedResults.NotFound();
        }

        try
        {
            storefront.AddDomain(request.Host, request.Canonical, time.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.Ok(ToResponse(storefront));
        }
        catch (CatalogRuleException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.Host)] = [ex.Message] });
        }
    }

    private static async Task<Results<Ok<StorefrontReadinessResponse>, NotFound>> Readiness(
        Guid id,
        CatalogDbContext db,
        CancellationToken cancellationToken)
    {
        var storefront = await db.Storefronts.AsNoTracking().Include(s => s.Domains).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (storefront is null)
        {
            return TypedResults.NotFound();
        }

        var result = storefront.CheckReadiness();
        return TypedResults.Ok(new StorefrontReadinessResponse(result.IsReady, result.MissingRequirements));
    }

    private static Task<Results<Ok<StorefrontResponse>, NotFound, ValidationProblem>> Preview(Guid id, CatalogDbContext db, TimeProvider time, CancellationToken cancellationToken) =>
        Transition(id, db, s => s.MoveToPreview(time.GetUtcNow()), cancellationToken);

    private static Task<Results<Ok<StorefrontResponse>, NotFound, ValidationProblem>> Activate(Guid id, CatalogDbContext db, TimeProvider time, CancellationToken cancellationToken) =>
        Transition(id, db, s => s.Activate(time.GetUtcNow()), cancellationToken);

    private static Task<Results<Ok<StorefrontResponse>, NotFound, ValidationProblem>> Pause(Guid id, CatalogDbContext db, TimeProvider time, CancellationToken cancellationToken) =>
        Transition(id, db, s => s.Pause(time.GetUtcNow()), cancellationToken);

    private static Task<Results<Ok<StorefrontResponse>, NotFound, ValidationProblem>> Archive(Guid id, CatalogDbContext db, TimeProvider time, CancellationToken cancellationToken) =>
        Transition(id, db, s => s.Archive(time.GetUtcNow()), cancellationToken);

    private static async Task<Results<Ok<StorefrontResponse>, NotFound, ValidationProblem>> Transition(
        Guid id,
        CatalogDbContext db,
        Action<Storefront> transition,
        CancellationToken cancellationToken)
    {
        var storefront = await db.Storefronts.Include(s => s.Domains).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (storefront is null)
        {
            return TypedResults.NotFound();
        }

        try
        {
            transition(storefront);
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.Ok(ToResponse(storefront));
        }
        catch (CatalogRuleException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["state"] = [ex.Message] });
        }
    }

    private static async Task<Results<Created<ProductPublicationResponse>, NotFound, Conflict<string>, ValidationProblem>> AssignProduct(
        Guid id,
        AssignProductRequest request,
        CatalogDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var storefront = await db.Storefronts.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        var product = await db.Products.Include(p => p.Variants).SingleOrDefaultAsync(p => p.Id == request.ProductId, cancellationToken);
        if (storefront is null || product is null || storefront.TenantId != product.TenantId)
        {
            return TypedResults.NotFound();
        }

        if (await db.ProductPublications.AnyAsync(p => p.StorefrontId == id && p.ProductId == request.ProductId, cancellationToken))
        {
            return TypedResults.Conflict("Product is already assigned to this storefront.");
        }

        try
        {
            var publication = ProductPublication.Assign(storefront.TenantId, id, product, time.GetUtcNow());
            publication.SetOverrides(request.SlugOverride, request.TitleOverride, request.DescriptionOverride, request.SeoTitle, request.SeoDescription, time.GetUtcNow());
            publication.SetFulfillment(request.FulfillmentSource, request.CountryOfOrigin, request.HarmonizedSystemCode, time.GetUtcNow());
            db.ProductPublications.Add(publication);
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.Created($"/admin/storefronts/{id}/products/{product.Id}", ToPublicationResponse(publication));
        }
        catch (CatalogRuleException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [nameof(request.ProductId)] = [ex.Message] });
        }
    }

    private static async Task<Results<Ok<ProductPublicationReadinessResponse>, NotFound>> ProductReadiness(
        Guid id,
        Guid productId,
        CatalogDbContext db,
        CancellationToken cancellationToken)
    {
        var publication = await db.ProductPublications.AsNoTracking().Include(p => p.Variants)
            .SingleOrDefaultAsync(p => p.StorefrontId == id && p.ProductId == productId, cancellationToken);
        var product = await db.Products.AsNoTracking().Include(p => p.Variants).SingleOrDefaultAsync(p => p.Id == productId, cancellationToken);
        if (publication is null || product is null)
        {
            return TypedResults.NotFound();
        }

        var readiness = publication.CheckReadiness(product);
        return TypedResults.Ok(new ProductPublicationReadinessResponse(readiness.IsReady, readiness.MissingRequirements));
    }

    private static Task<Results<Ok<ProductPublicationResponse>, NotFound, ValidationProblem>> PublishProduct(
        Guid id, Guid productId, CatalogDbContext db, TimeProvider time, CancellationToken cancellationToken) =>
        ProductTransition(id, productId, db, (publication, product) => publication.Publish(product, time.GetUtcNow()), cancellationToken);

    private static Task<Results<Ok<ProductPublicationResponse>, NotFound, ValidationProblem>> UnpublishProduct(
        Guid id, Guid productId, CatalogDbContext db, TimeProvider time, CancellationToken cancellationToken) =>
        ProductTransition(id, productId, db, (publication, _) => publication.Unpublish(time.GetUtcNow()), cancellationToken);

    private static async Task<Results<Ok<ProductPublicationResponse>, NotFound, ValidationProblem>> ProductTransition(
        Guid storefrontId,
        Guid productId,
        CatalogDbContext db,
        Action<ProductPublication, Product> transition,
        CancellationToken cancellationToken)
    {
        var publication = await db.ProductPublications.Include(p => p.Variants)
            .SingleOrDefaultAsync(p => p.StorefrontId == storefrontId && p.ProductId == productId, cancellationToken);
        var product = await db.Products.Include(p => p.Variants).SingleOrDefaultAsync(p => p.Id == productId, cancellationToken);
        if (publication is null || product is null)
        {
            return TypedResults.NotFound();
        }

        try
        {
            transition(publication, product);
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.Ok(ToPublicationResponse(publication));
        }
        catch (CatalogRuleException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [nameof(productId)] = [ex.Message] });
        }
    }

    private static ProductPublicationResponse ToPublicationResponse(ProductPublication publication) => new(
        publication.Id,
        publication.TenantId,
        publication.StorefrontId,
        publication.ProductId,
        publication.State,
        publication.SlugOverride,
        publication.TitleOverride,
        publication.SeoTitle,
        publication.SeoDescription,
        publication.FulfillmentSource,
        publication.CountryOfOrigin,
        publication.HarmonizedSystemCode,
        publication.Variants.Select(v => new ProductPublicationVariantResponse(v.VariantId, v.Visible, v.SkuOverride)).ToList(),
        publication.PublishedAt);

    private static StorefrontResponse ToResponse(Storefront storefront) => new(
        storefront.Id,
        storefront.TenantId,
        storefront.Name,
        storefront.State,
        storefront.Visibility,
        storefront.PublicUrl,
        storefront.Currency,
        storefront.TaxRegime,
        storefront.TaxRateBasisPoints,
        storefront.Domains.Select(d => new StorefrontDomainResponse(d.Id, d.Host, d.Canonical)).ToList(),
        storefront.CreatedAt,
        storefront.UpdatedAt,
        storefront.ActivatedAt);
}

public sealed record CreateStorefrontRequest(
    [property: Required] Guid TenantId,
    [property: Required, StringLength(120, MinimumLength = 2)] string Name,
    StorefrontVisibility Visibility,
    string? AccessPasswordHash,
    string? PublicUrl = null,
    string? Currency = "EUR",
    StorefrontTaxRegime TaxRegime = StorefrontTaxRegime.None,
    int TaxRateBasisPoints = 0);

public sealed record UpdateStorefrontRequest(
    [property: Required, StringLength(120, MinimumLength = 2)] string Name,
    StorefrontVisibility Visibility,
    string? AccessPasswordHash,
    string? PublicUrl,
    [property: Required, StringLength(3, MinimumLength = 3)] string Currency,
    StorefrontTaxRegime TaxRegime,
    int TaxRateBasisPoints);

public sealed record AddStorefrontDomainRequest(
    [property: Required, StringLength(253, MinimumLength = 3)] string Host,
    bool Canonical);

public sealed record StorefrontResponse(
    Guid Id,
    Guid TenantId,
    string Name,
    StorefrontState State,
    StorefrontVisibility Visibility,
    string PublicUrl,
    string Currency,
    StorefrontTaxRegime TaxRegime,
    int TaxRateBasisPoints,
    IReadOnlyList<StorefrontDomainResponse> Domains,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ActivatedAt);

public sealed record StorefrontDomainResponse(Guid Id, string Host, bool Canonical);

public sealed record StorefrontReadinessResponse(bool IsReady, IReadOnlyList<string> MissingRequirements);

public sealed record AssignProductRequest(
    [property: Required] Guid ProductId,
    string? SlugOverride,
    string? TitleOverride,
    string? DescriptionOverride,
    string? SeoTitle,
    string? SeoDescription,
    FulfilmentType FulfillmentSource,
    string? CountryOfOrigin,
    string? HarmonizedSystemCode);

public sealed record ProductPublicationResponse(
    Guid Id,
    Guid TenantId,
    Guid StorefrontId,
    Guid ProductId,
    PublicationState State,
    string? SlugOverride,
    string? TitleOverride,
    string? SeoTitle,
    string? SeoDescription,
    FulfilmentType FulfillmentSource,
    string? CountryOfOrigin,
    string? HarmonizedSystemCode,
    IReadOnlyList<ProductPublicationVariantResponse> Variants,
    DateTimeOffset? PublishedAt);

public sealed record ProductPublicationVariantResponse(Guid VariantId, bool Visible, string? SkuOverride);

public sealed record ProductPublicationReadinessResponse(bool IsReady, IReadOnlyList<string> MissingRequirements);
