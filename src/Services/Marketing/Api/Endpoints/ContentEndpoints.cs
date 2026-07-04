using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.BuildingBlocks.Infrastructure.Export;
using ThreeCommerce.Marketing.Domain;
using ThreeCommerce.Marketing.Infrastructure;

namespace ThreeCommerce.Marketing.Api.Endpoints;

/// <summary>
/// Publishable content (def_5 / mt5_7): versioned drafts, publish/schedule/rollback, and signed
/// expiring preview links. Preview pages are read-only and noindex (mt5_7 GOTCHA); the link signs
/// contentId+version with the shared SignedDownload HMAC — no server-side session.
/// </summary>
public static class ContentEndpoints
{
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromHours(1);

    public static IEndpointRouteBuilder MapContent(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/admin/content").WithTags("Content")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);
        admin.MapGet("/", List).WithSummary("List tenant content with status.");
        admin.MapPost("/", Create).WithSummary("Create content (version 1 draft).");
        admin.MapGet("/{id:guid}", Get).WithSummary("Content detail incl. draft payload + version list.");
        admin.MapPut("/{id:guid}/draft", SaveDraft).WithSummary("Save a new draft version (history retained; clears any schedule).");
        admin.MapPost("/{id:guid}/publish", Publish).WithSummary("Publish the current draft.");
        admin.MapPost("/{id:guid}/schedule", Schedule).WithSummary("Schedule the current draft to publish at a future time (≤60s sweep latency).");
        admin.MapPost("/{id:guid}/rollback", Rollback).WithSummary("Point the live version back to an existing version.");
        admin.MapPost("/{id:guid}/preview-token", PreviewToken).WithSummary("Mint a signed, expiring, read-only preview link for the current draft.");

        // Anonymous render surface: published-by-key for storefront pages; signed preview for drafts.
        app.MapGet("/content/{key}", Published).WithTags("Content")
            .WithSummary("Published payload by key (storefront rendering).");
        app.MapGet("/content/preview/{id:guid}/{version:int}", Preview).WithTags("Content")
            .WithSummary("Signed draft preview (noindex, read-only).");
        return app;
    }

    private static async Task<Ok<List<ContentSummaryDto>>> List(
        Guid tenantId, PublishingService publishing, CancellationToken ct)
    {
        var items = await publishing.ListAsync(tenantId, ct);
        return TypedResults.Ok(items.Select(c => new ContentSummaryDto(
            c.Id, c.Key, c.Status, c.DraftVersion, c.PublishedVersion, c.ScheduledAt, c.UpdatedAt)).ToList());
    }

    private static async Task<Results<Created<ContentDetailDto>, BadRequest<string>>> Create(
        CreateContentRequest request, Guid tenantId, PublishingService publishing, CancellationToken ct)
    {
        try
        {
            var content = await publishing.CreateAsync(tenantId, request.Key, request.Payload, ct);
            return TypedResults.Created($"/admin/content/{content.Id}", Detail(content));
        }
        catch (MarketingRuleException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private static async Task<Results<Ok<ContentDetailDto>, NotFound>> Get(
        Guid id, Guid tenantId, PublishingService publishing, CancellationToken ct)
    {
        var content = await publishing.GetAsync(tenantId, id, ct);
        return content is null ? TypedResults.NotFound() : TypedResults.Ok(Detail(content));
    }

    private static Task<Results<Ok<ContentDetailDto>, NotFound, BadRequest<string>>> SaveDraft(
        Guid id, Guid tenantId, SavePayloadRequest request, PublishingService publishing, TimeProvider time, CancellationToken ct) =>
        Mutate(id, tenantId, publishing, c => c.SaveDraft(request.Payload, time.GetUtcNow()), ct);

    private static Task<Results<Ok<ContentDetailDto>, NotFound, BadRequest<string>>> Publish(
        Guid id, Guid tenantId, PublishingService publishing, TimeProvider time, CancellationToken ct) =>
        Mutate(id, tenantId, publishing, c => c.Publish(time.GetUtcNow()), ct);

    private static Task<Results<Ok<ContentDetailDto>, NotFound, BadRequest<string>>> Schedule(
        Guid id, Guid tenantId, ScheduleRequest request, PublishingService publishing, TimeProvider time, CancellationToken ct) =>
        Mutate(id, tenantId, publishing, c => c.SchedulePublish(request.At, time.GetUtcNow()), ct);

    private static Task<Results<Ok<ContentDetailDto>, NotFound, BadRequest<string>>> Rollback(
        Guid id, Guid tenantId, RollbackRequest request, PublishingService publishing, TimeProvider time, CancellationToken ct) =>
        Mutate(id, tenantId, publishing, c => c.Rollback(request.Version, time.GetUtcNow()), ct);

    private static async Task<Results<Ok<PreviewTokenDto>, NotFound>> PreviewToken(
        Guid id, Guid tenantId, PublishingService publishing, TimeProvider time, IConfiguration config, CancellationToken ct)
    {
        var content = await publishing.GetAsync(tenantId, id, ct);
        if (content is null)
        {
            return TypedResults.NotFound();
        }

        var preview = content.Preview(time.GetUtcNow(), PreviewLifetime);
        var token = SignedDownload.CreateToken(PreviewSecret(config), $"{preview.ContentId}:{preview.Version}", preview.ExpiresAt);
        return TypedResults.Ok(new PreviewTokenDto(
            preview.ContentId, preview.Version, preview.ExpiresAt,
            $"/content/preview/{preview.ContentId}/{preview.Version}?token={Uri.EscapeDataString(token)}"));
    }

    private static async Task<Results<Ok<PublishedDto>, NotFound>> Published(
        string key, Guid tenantId, PublishingService publishing, CancellationToken ct)
    {
        var published = await publishing.GetPublishedAsync(tenantId, key, ct);
        return published is not var (contentKey, version, payload)
            ? TypedResults.NotFound()
            : TypedResults.Ok(new PublishedDto(contentKey, version, payload));
    }

    private static async Task<Results<Ok<PublishedDto>, NotFound, UnauthorizedHttpResult>> Preview(
        Guid id, int version, string token, PublishingService publishing, TimeProvider time, IConfiguration config, CancellationToken ct)
    {
        if (!SignedDownload.IsValid(PreviewSecret(config), $"{id}:{version}", token, time.GetUtcNow()))
        {
            return TypedResults.Unauthorized();
        }

        var payload = await publishing.GetVersionPayloadAsync(id, version, ct);
        return payload is null ? TypedResults.NotFound() : TypedResults.Ok(new PublishedDto($"preview:{id}", version, payload));
    }

    private static async Task<Results<Ok<ContentDetailDto>, NotFound, BadRequest<string>>> Mutate(
        Guid id, Guid tenantId, PublishingService publishing, Action<PublishableContent> mutation, CancellationToken ct)
    {
        try
        {
            var content = await publishing.MutateAsync(tenantId, id, mutation, ct);
            return content is null ? TypedResults.NotFound() : TypedResults.Ok(Detail(content));
        }
        catch (MarketingRuleException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private static string PreviewSecret(IConfiguration config) =>
        config["Publishing:PreviewSecret"] ?? "dev-preview-secret"; // rotate outside dev (docs/help/deployment.md)

    private static ContentDetailDto Detail(PublishableContent content) => new(
        content.Id, content.Key, content.Status, content.DraftVersion, content.PublishedVersion,
        content.ScheduledAt, content.DraftPayload, content.PublishedPayload, content.Versions.Order().ToList(), content.UpdatedAt);
}

public record CreateContentRequest(
    [property: Required, MaxLength(128)] string Key,
    [property: Required] string Payload);

public record SavePayloadRequest([property: Required] string Payload);

public record ScheduleRequest([property: Required] DateTimeOffset At);

public record RollbackRequest([property: Required] int Version);

/// <summary>Status is numeric on the wire (AGENTS.md invariant).</summary>
public record ContentSummaryDto(Guid Id, string Key, PublishStatus Status, int DraftVersion, int? PublishedVersion, DateTimeOffset? ScheduledAt, DateTimeOffset UpdatedAt);

public record ContentDetailDto(
    Guid Id, string Key, PublishStatus Status, int DraftVersion, int? PublishedVersion, DateTimeOffset? ScheduledAt,
    string DraftPayload, string? PublishedPayload, List<int> Versions, DateTimeOffset UpdatedAt);

public record PreviewTokenDto(Guid ContentId, int Version, DateTimeOffset ExpiresAt, string PreviewPath);

public record PublishedDto(string Key, int Version, string Payload);
