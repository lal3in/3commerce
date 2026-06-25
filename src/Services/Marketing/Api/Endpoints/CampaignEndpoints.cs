using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Marketing.Domain;
using ThreeCommerce.Marketing.Infrastructure;

namespace ThreeCommerce.Marketing.Api.Endpoints;

/// <summary>Campaign admin (mt5_1): create + lifecycle. Tenant-scoped.</summary>
public static class CampaignEndpoints
{
    public static IEndpointRouteBuilder MapCampaigns(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/campaigns").WithTags("Campaigns")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);

        group.MapGet("/", List);
        group.MapPost("/", Create);
        group.MapPost("/{id:guid}/activate", (Guid id, MarketingDbContext db, TimeProvider clock, IConfiguration cfg, CancellationToken ct) =>
            Transition(id, db, clock, cfg, ct, (c, now) => c.Activate(now)));
        group.MapPost("/{id:guid}/pause", (Guid id, MarketingDbContext db, TimeProvider clock, IConfiguration cfg, CancellationToken ct) =>
            Transition(id, db, clock, cfg, ct, (c, now) => c.Pause(now)));
        group.MapPost("/{id:guid}/end", (Guid id, MarketingDbContext db, TimeProvider clock, IConfiguration cfg, CancellationToken ct) =>
            Transition(id, db, clock, cfg, ct, (c, now) => c.End(now)));
        return app;
    }

    private static async Task<Ok<List<CampaignDto>>> List(
        Guid? tenantId, MarketingDbContext db, IConfiguration config, CancellationToken ct)
    {
        var tenant = tenantId ?? DefaultTenantId(config);
        var campaigns = await db.Campaigns.AsNoTracking()
            .Where(c => c.TenantId == tenant).OrderByDescending(c => c.CreatedAt).ToListAsync(ct);
        return TypedResults.Ok(campaigns.Select(ToDto).ToList());
    }

    private static async Task<Results<Created<CampaignDto>, BadRequest<string>>> Create(
        CreateCampaignRequest request, MarketingDbContext db, TimeProvider clock, IConfiguration config, CancellationToken ct)
    {
        try
        {
            var campaign = Campaign.Create(
                request.TenantId ?? DefaultTenantId(config), request.Cid, request.Name, clock.GetUtcNow(), request.StartsAt, request.EndsAt);
            db.Campaigns.Add(campaign);
            await db.SaveChangesAsync(ct);
            return TypedResults.Created($"/admin/campaigns/{campaign.Id}", ToDto(campaign));
        }
        catch (MarketingRuleException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private static async Task<Results<Ok<CampaignDto>, NotFound, BadRequest<string>>> Transition(
        Guid id, MarketingDbContext db, TimeProvider clock, IConfiguration config, CancellationToken ct, Action<Campaign, DateTimeOffset> apply)
    {
        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (campaign is null)
        {
            return TypedResults.NotFound();
        }

        try
        {
            apply(campaign, clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            return TypedResults.Ok(ToDto(campaign));
        }
        catch (MarketingRuleException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private static Guid DefaultTenantId(IConfiguration config) =>
        Guid.TryParse(config["Tenancy:DefaultTenantId"], out var tenantId)
            ? tenantId
            : new Guid("00000000-0000-0000-0000-000000000001");

    private static CampaignDto ToDto(Campaign c) =>
        new(c.Id, c.Cid, c.Name, c.Status.ToString(), c.StartsAt, c.EndsAt, c.CreatedAt);
}

public record CreateCampaignRequest(
    Guid? TenantId, [property: Required] string Cid, [property: Required] string Name, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt);

public record CampaignDto(Guid Id, string Cid, string Name, string Status, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, DateTimeOffset CreatedAt);
