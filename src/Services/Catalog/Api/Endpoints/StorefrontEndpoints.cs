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

        // Public (anon): the storefront app resolves its active storefront's currency/tax config
        // by canonical domain host (production) or by the PublicUrl path slug (local /{slug} demo).
        var publicGroup = app.MapGroup("/storefronts").WithTags("Storefronts");
        publicGroup.MapGet("/public", GetPublicConfig);
        // The languages a storefront's UI can be set to (i18n_0) — a static vocabulary, anon-readable
        // so the storefront app and the admin language pickers share one source of truth.
        publicGroup.MapGet("/languages", GetSupportedLanguages);

        return app;
    }

    private static Ok<List<SupportedLanguageResponse>> GetSupportedLanguages() =>
        TypedResults.Ok(SupportedLanguages.All.Select(l => new SupportedLanguageResponse(l.Code, l.Label)).ToList());

    private static async Task<Results<Ok<PublicStorefrontResponse>, NotFound>> GetPublicConfig(
        string? host,
        string? slug,
        string? currency,
        CatalogDbContext db,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host) && string.IsNullOrWhiteSpace(slug) && string.IsNullOrWhiteSpace(currency))
        {
            return TypedResults.NotFound();
        }

        // Only live-ish storefronts expose a public config; never Draft/Paused/Archived.
        var candidates = await db.Storefronts.AsNoTracking()
            .Include(s => s.Domains)
            .Where(s => s.State == StorefrontState.Active || s.State == StorefrontState.Preview)
            .ToListAsync(cancellationToken);

        Storefront? storefront = null;
        if (!string.IsNullOrWhiteSpace(host))
        {
            var h = host.Trim();
            storefront = candidates.FirstOrDefault(s => s.Domains.Any(d => string.Equals(d.Host, h, StringComparison.OrdinalIgnoreCase)));
        }

        if (storefront is null && !string.IsNullOrWhiteSpace(slug))
        {
            var s = slug.Trim();
            storefront = candidates.FirstOrDefault(x => string.Equals(PublicUrlSlug(x.PublicUrl), s, StringComparison.OrdinalIgnoreCase));
        }

        if (storefront is null && !string.IsNullOrWhiteSpace(currency))
        {
            var c = currency.Trim();
            storefront = candidates.FirstOrDefault(x => string.Equals(x.Currency, c, StringComparison.OrdinalIgnoreCase));
        }

        return storefront is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(new PublicStorefrontResponse(
                storefront.Id, storefront.TenantId, storefront.Name, storefront.PublicUrl, storefront.Currency,
                storefront.TaxRegime, storefront.TaxRateBasisPoints, storefront.DefaultLanguage, storefront.ShipToCountries));
    }

    // Last non-empty path segment of the PublicUrl, e.g. "http://localhost:3000/au" -> "au".
    private static string PublicUrlSlug(string publicUrl)
    {
        if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        var path = uri.AbsolutePath.Trim('/');
        return path.Length == 0 ? string.Empty : path.Split('/')[^1];
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
        IPublishEndpoint publisher,
        IAuditRecorder audit,
        ClaimsPrincipal user,
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
            storefront.SetDefaultLanguage(request.DefaultLanguage, time.GetUtcNow());
            storefront.SetShipToCountries(request.ShipToCountries, time.GetUtcNow());
            db.Storefronts.Add(storefront);
            await PublishConfigAsync(publisher, storefront, cancellationToken); // before Save so it lands in the outbox tx
            await audit.RecordAsync(user.Mutation(
                storefront.TenantId, "Storefront", storefront.Id.ToString(), "catalog.storefront.create", storefront.Name), cancellationToken);
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
        IPublishEndpoint publisher,
        IAuditRecorder audit,
        ClaimsPrincipal user,
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
            storefront.SetDefaultLanguage(request.DefaultLanguage, now); // null = leave as-is (language is not commerce)
            storefront.SetShipToCountries(request.ShipToCountries, now); // null = leave as-is (older client can't wipe it)
            await PublishConfigAsync(publisher, storefront, cancellationToken); // before Save so it lands in the outbox tx
            await audit.RecordAsync(user.Mutation(
                storefront.TenantId, "Storefront", storefront.Id.ToString(), "catalog.storefront.update", storefront.Name), cancellationToken);
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
        IAuditRecorder audit,
        ClaimsPrincipal user,
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
            var newDomain = storefront.AddDomain(request.Host, request.Canonical, time.GetUtcNow());
            // A new client-keyed child added via a TRACKED parent's nav is inferred Modified by
            // DetectChanges (Guid PK is store-generated) → EF issues an UPDATE that affects 0 rows →
            // DbUpdateConcurrencyException. Add it through the context directly so it's marked Added.
            db.StorefrontDomains.Add(newDomain);
            await audit.RecordAsync(user.Mutation(
                storefront.TenantId, "Storefront", storefront.Id.ToString(), "catalog.storefront.domain_add", request.Host), cancellationToken);
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

    private static Task<Results<Ok<StorefrontResponse>, NotFound, ValidationProblem>> Preview(Guid id, CatalogDbContext db, IPublishEndpoint publisher, IAuditRecorder audit, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken) =>
        Transition(id, "preview", db, publisher, audit, user, s => s.MoveToPreview(time.GetUtcNow()), cancellationToken);

    private static Task<Results<Ok<StorefrontResponse>, NotFound, ValidationProblem>> Activate(Guid id, CatalogDbContext db, IPublishEndpoint publisher, IAuditRecorder audit, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken) =>
        Transition(id, "activate", db, publisher, audit, user, s => s.Activate(time.GetUtcNow()), cancellationToken);

    private static Task<Results<Ok<StorefrontResponse>, NotFound, ValidationProblem>> Pause(Guid id, CatalogDbContext db, IPublishEndpoint publisher, IAuditRecorder audit, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken) =>
        Transition(id, "pause", db, publisher, audit, user, s => s.Pause(time.GetUtcNow()), cancellationToken);

    private static Task<Results<Ok<StorefrontResponse>, NotFound, ValidationProblem>> Archive(Guid id, CatalogDbContext db, IPublishEndpoint publisher, IAuditRecorder audit, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken) =>
        Transition(id, "archive", db, publisher, audit, user, s => s.Archive(time.GetUtcNow()), cancellationToken);

    private static async Task<Results<Ok<StorefrontResponse>, NotFound, ValidationProblem>> Transition(
        Guid id,
        string transitionName,
        CatalogDbContext db,
        IPublishEndpoint publisher,
        IAuditRecorder audit,
        ClaimsPrincipal user,
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
            await PublishConfigAsync(publisher, storefront, cancellationToken); // before Save so it lands in the outbox tx
            await audit.RecordAsync(user.Mutation(
                storefront.TenantId, "Storefront", storefront.Id.ToString(), $"catalog.storefront.{transitionName}", storefront.Name), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.Ok(ToResponse(storefront));
        }
        catch (CatalogRuleException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["state"] = [ex.Message] });
        }
    }

    // Project the storefront's shopper-facing config to other services (ADR-0008), e.g. Ordering's tax.
    private static Task PublishConfigAsync(IPublishEndpoint publisher, Storefront s, CancellationToken ct) =>
        publisher.Publish(new StorefrontConfigChanged(
            s.Id, s.TenantId, s.Name, s.Currency, s.TaxRateBasisPoints,
            s.State is StorefrontState.Active or StorefrontState.Preview,
            TaxInclusive: s.TaxRegime is StorefrontTaxRegime.AuGst or StorefrontTaxRegime.EuVat, // ADR-0038
            ShipToCountries: s.ShipToCountries.ToArray()), ct);

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
        Guid id, Guid productId, CatalogDbContext db, IAuditRecorder audit, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken) =>
        ProductTransition(id, productId, "publish", db, audit, user, (publication, product) => publication.Publish(product, time.GetUtcNow()), cancellationToken);

    private static Task<Results<Ok<ProductPublicationResponse>, NotFound, ValidationProblem>> UnpublishProduct(
        Guid id, Guid productId, CatalogDbContext db, IAuditRecorder audit, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken) =>
        ProductTransition(id, productId, "unpublish", db, audit, user, (publication, _) => publication.Unpublish(time.GetUtcNow()), cancellationToken);

    private static async Task<Results<Ok<ProductPublicationResponse>, NotFound, ValidationProblem>> ProductTransition(
        Guid storefrontId,
        Guid productId,
        string transitionName,
        CatalogDbContext db,
        IAuditRecorder audit,
        ClaimsPrincipal user,
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
            await audit.RecordAsync(user.Mutation(
                publication.TenantId, "ProductPublication", publication.Id.ToString(), $"catalog.product.{transitionName}", product.Title), cancellationToken);
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
        storefront.DefaultLanguage,
        storefront.Domains.Select(d => new StorefrontDomainResponse(d.Id, d.Host, d.Canonical)).ToList(),
        storefront.ShipToCountries.ToList(),
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
    int TaxRateBasisPoints = 0,
    // BCP-47 default UI language (i18n_0); omit → "en". Independent of Currency/TaxRegime.
    [property: StringLength(16, MinimumLength = 2)] string? DefaultLanguage = null,
    // Ship-to allowlist (ISO 3166-1 alpha-2); omit/empty → ships worldwide.
    IReadOnlyList<string>? ShipToCountries = null);

public sealed record UpdateStorefrontRequest(
    [property: Required, StringLength(120, MinimumLength = 2)] string Name,
    StorefrontVisibility Visibility,
    string? AccessPasswordHash,
    string? PublicUrl,
    [property: Required, StringLength(3, MinimumLength = 3)] string Currency,
    StorefrontTaxRegime TaxRegime,
    int TaxRateBasisPoints,
    // Optional: omitted → the storefront keeps its current language (a currency/tax edit never resets it).
    [property: StringLength(16, MinimumLength = 2)] string? DefaultLanguage = null,
    // Optional: null → the storefront keeps its current ship-to allowlist; [] → clears it (worldwide).
    IReadOnlyList<string>? ShipToCountries = null);

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
    string DefaultLanguage,
    IReadOnlyList<StorefrontDomainResponse> Domains,
    IReadOnlyList<string> ShipToCountries,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ActivatedAt);

public sealed record StorefrontDomainResponse(Guid Id, string Host, bool Canonical);

// Minimal, anon-safe public view of a storefront's shopper-facing config (currency + tax + language).
// Id/TenantId are non-secret identifiers the storefront forwards at checkout for order attribution.
// DefaultLanguage is the storefront's UI language before any per-session shopper override (i18n_0).
public sealed record PublicStorefrontResponse(
    Guid Id,
    Guid TenantId,
    string Name,
    string PublicUrl,
    string Currency,
    StorefrontTaxRegime TaxRegime,
    int TaxRateBasisPoints,
    string DefaultLanguage,
    // Ship-to allowlist (ISO 3166-1 alpha-2); empty = worldwide. The storefront app filters its checkout
    // country picker to these and the checkout API rejects a ship-to country outside the list.
    IReadOnlyList<string> ShipToCountries);

// The languages a storefront's UI can be set to (i18n_0). Label is an endonym ("中文"), not a translation.
public sealed record SupportedLanguageResponse(string Code, string Label);

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
