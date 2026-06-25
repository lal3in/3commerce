using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Marketing.Domain;
using ThreeCommerce.Marketing.Infrastructure;

namespace ThreeCommerce.Marketing.Api.Endpoints;

/// <summary>Short link admin (mt5_3): create + list. Destination must be a registered storefront host.</summary>
public static class ShortLinkEndpoints
{
    public static IEndpointRouteBuilder MapShortLinks(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/short-links").WithTags("ShortLinks")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);

        group.MapGet("/", List);
        group.MapPost("/", Create);
        return app;
    }

    private static async Task<Ok<List<ShortLinkDto>>> List(
        Guid? tenantId, MarketingDbContext db, IConfiguration config, CancellationToken ct)
    {
        var tenant = tenantId ?? DefaultTenantId(config);
        var links = await db.ShortLinks.AsNoTracking()
            .Where(l => l.TenantId == tenant).OrderByDescending(l => l.CreatedAt).ToListAsync(ct);
        return TypedResults.Ok(links.Select(ToDto).ToList());
    }

    private static async Task<Results<Created<ShortLinkDto>, BadRequest<string>>> Create(
        CreateShortLinkRequest request, MarketingDbContext db, TimeProvider clock, IConfiguration config, CancellationToken ct)
    {
        var code = ShortCode.IsValid(request.Code) ? request.Code! : ShortCode.Generate();
        try
        {
            var link = ShortLink.Create(
                request.TenantId ?? DefaultTenantId(config), code, request.Destination, AllowedHosts(config), clock.GetUtcNow(),
                request.Cid, request.ExpiresAt);
            db.ShortLinks.Add(link);
            await db.SaveChangesAsync(ct);
            return TypedResults.Created($"/admin/short-links/{link.Id}", ToDto(link));
        }
        catch (MarketingRuleException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private static IReadOnlySet<string> AllowedHosts(IConfiguration config) =>
        (config["Marketing:AllowedHosts"] ?? "localhost")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(h => h.ToLowerInvariant()).ToHashSet();

    private static Guid DefaultTenantId(IConfiguration config) =>
        Guid.TryParse(config["Tenancy:DefaultTenantId"], out var tenantId)
            ? tenantId
            : new Guid("00000000-0000-0000-0000-000000000001");

    private static ShortLinkDto ToDto(ShortLink l) =>
        new(l.Id, l.Code, l.Destination, l.Cid, l.Status.ToString(), l.ExpiresAt, l.ClickCount, l.CreatedAt);
}

public record CreateShortLinkRequest(
    Guid? TenantId, string? Code, [property: Required, Url] string Destination, string? Cid, DateTimeOffset? ExpiresAt);

public record ShortLinkDto(
    Guid Id, string Code, string Destination, string? Cid, string Status, DateTimeOffset? ExpiresAt, long ClickCount, DateTimeOffset CreatedAt);
