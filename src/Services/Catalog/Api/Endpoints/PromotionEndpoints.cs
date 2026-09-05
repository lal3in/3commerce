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

/// <summary>
/// Threshold promotions (ADR-0051): tenant-authored rules that grant free shipping and/or a discount
/// once a cart (storefront scope) or one product's lines (product scope) clear a money and/or quantity
/// threshold. Admin-managed here; Ordering evaluates them off the projected PromotionCopy read model,
/// never by querying Catalog (ADR-0008).
/// </summary>
public static class PromotionEndpoints
{
    public static IEndpointRouteBuilder MapPromotions(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/promotions").WithTags("Promotions")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);

        group.MapGet("/", List);
        group.MapPost("/", Create);
        group.MapPut("/{id:guid}", Update);
        return app;
    }

    private static async Task<Ok<List<PromotionDto>>> List(
        Guid? tenantId, Guid? storefrontId, CatalogDbContext db, IConfiguration config, CancellationToken ct)
    {
        var tid = tenantId ?? DefaultTenantId(config);
        var query = db.Promotions.AsNoTracking().Where(p => p.TenantId == tid);
        if (storefrontId is { } sid)
        {
            query = query.Where(p => p.StorefrontId == sid);
        }

        // Name alone leaves same-named promotions in an arbitrary, run-to-run order; the Guid v7 id is a
        // stable creation-ordered secondary key so the admin table renders identically across reloads.
        var promotions = await query.OrderBy(p => p.Name).ThenBy(p => p.Id).ToListAsync(ct);
        var productIds = promotions.Where(p => p.ProductId is not null).Select(p => p.ProductId!.Value).Distinct().ToList();
        var titles = await db.Products.AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Title, ct);
        return TypedResults.Ok(promotions
            .Select(p => ToDto(p, p.ProductId is { } pid ? titles.GetValueOrDefault(pid, string.Empty) : string.Empty))
            .ToList());
    }

    private static async Task<Results<Created<PromotionDto>, BadRequest<string>>> Create(
        CreatePromotionRequest request, CatalogDbContext db, IConfiguration config, TimeProvider clock,
        IPublishEndpoint publisher, IAuditRecorder audit, ClaimsPrincipal user, CancellationToken ct)
    {
        try
        {
            var now = clock.GetUtcNow();
            var tenantId = request.TenantId ?? DefaultTenantId(config);
            // A promotion is identified for operators by its (TenantId, StorefrontId, Name). The same name
            // on a different storefront is a legitimately distinct campaign; the EXACT same key repeated is
            // a duplicate — reject it so a re-run seed can't pile several rows behind one shopper-visible
            // label (which would silently stack as several combinable promotions).
            var duplicate = await db.Promotions.AsNoTracking().AnyAsync(
                p => p.TenantId == tenantId
                    && p.StorefrontId == request.StorefrontId
                    && p.Name == request.Name.Trim(),
                ct);
            if (duplicate)
            {
                return TypedResults.BadRequest(
                    "A promotion with this name already exists for this tenant and storefront. "
                    + "Update the existing promotion instead of creating a duplicate.");
            }

            // Coupon codes (ADR-0052) are unique per tenant among the promotions that carry one, so a code
            // always resolves to exactly ONE promotion. The database enforces it with a filtered unique
            // index; this probe turns the race-free-but-opaque 23505 into an operator-readable 400.
            var code = Promotion.NormalizeCode(request.Code);
            if (code is not null
                && await db.Promotions.AsNoTracking().AnyAsync(p => p.TenantId == tenantId && p.Code == code, ct))
            {
                return TypedResults.BadRequest(
                    $"Coupon code '{code}' is already used by another promotion for this tenant. "
                    + "Pick a different code, or edit the promotion that owns it.");
            }

            var promotion = Promotion.Create(
                tenantId, request.Name, request.Currency, ToScope(request.Scope), request.ProductId, now);
            // Set the code BEFORE the threshold: a code-gated promotion is allowed to have no threshold
            // (the code is the gate), and SetThreshold consults IsCouponGated. A create with no code
            // never calls SetCode — clearing a code is an UPDATE concern with its own guard.
            if (code is not null)
            {
                promotion.SetCode(code, now);
            }

            promotion.SetUsageLimits(request.MaxRedemptions, request.MaxRedemptionsPerCustomer, now);
            promotion.SetThreshold(request.MinimumAmountMinor, request.MinimumQuantity, now);
            promotion.SetReward(request.GrantsFreeShipping, request.PercentOff, request.DiscountAmountMinor, now);
            promotion.SetCombinable(request.Combinable, now);
            promotion.SetStorefront(request.StorefrontId, now);
            promotion.SetActiveWindow(request.ActiveFrom, request.ActiveUntil, now);
            db.Promotions.Add(promotion);
            await audit.RecordAsync(user.Mutation(
                promotion.TenantId, "Promotion", promotion.Id.ToString(), "catalog.promotion.create", promotion.Name), ct);
            // Publish BEFORE Save so the PromotionChanged outbox row commits in the same transaction and
            // is actually delivered. Publishing after SaveChanges strands it in the change tracker
            // (never flushed) — Ordering's PromotionCopy projection then never fires and the promotion
            // silently never applies at checkout (the same outbox trap as the Offer/RMA paths).
            await publisher.Publish(ToEvent(promotion), ct);
            await db.SaveChangesAsync(ct);
            return TypedResults.Created($"/admin/promotions/{promotion.Id}", ToDto(promotion));
        }
        catch (CatalogRuleException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private static async Task<Results<Ok<PromotionDto>, NotFound, BadRequest<string>>> Update(
        Guid id, UpdatePromotionRequest request, CatalogDbContext db, TimeProvider clock,
        IPublishEndpoint publisher, IAuditRecorder audit, ClaimsPrincipal user, CancellationToken ct)
    {
        var promotion = await db.Promotions.SingleOrDefaultAsync(p => p.Id == id, ct);
        if (promotion is null)
        {
            return TypedResults.NotFound();
        }

        try
        {
            var now = clock.GetUtcNow();
            if (request.Name is { } name)
            {
                promotion.Rename(name, now);
            }

            // Coupon code + usage limits (ADR-0052) are each opt-in, because null is MEANINGFUL on both
            // (no code = automatic; no limit = unlimited) and a partial update must not silently wipe them.
            if (request.ApplyCode)
            {
                var newCode = Promotion.NormalizeCode(request.Code);
                if (newCode is not null && newCode != promotion.Code
                    && await db.Promotions.AsNoTracking().AnyAsync(
                        p => p.TenantId == promotion.TenantId && p.Code == newCode && p.Id != promotion.Id, ct))
                {
                    return TypedResults.BadRequest(
                        $"Coupon code '{newCode}' is already used by another promotion for this tenant.");
                }

                promotion.SetCode(request.Code, now);
            }

            if (request.ApplyUsageLimits)
            {
                promotion.SetUsageLimits(request.MaxRedemptions, request.MaxRedemptionsPerCustomer, now);
            }

            // Thresholds and rewards are each set as a UNIT (the domain enforces "at least one threshold"
            // and "at least one reward, percent XOR fixed" across the pair), so a partial update that sends
            // one half must send the other — the caller opts in by sending either field of the pair.
            if (request.MinimumAmountMinor is not null || request.MinimumQuantity is not null)
            {
                promotion.SetThreshold(
                    request.MinimumAmountMinor ?? promotion.MinimumAmountMinor,
                    request.MinimumQuantity ?? promotion.MinimumQuantity,
                    now);
            }

            if (request.GrantsFreeShipping is not null || request.PercentOff is not null || request.DiscountAmountMinor is not null)
            {
                promotion.SetReward(
                    request.GrantsFreeShipping ?? promotion.GrantsFreeShipping,
                    request.PercentOff ?? promotion.PercentOff,
                    request.DiscountAmountMinor ?? promotion.DiscountAmountMinor,
                    now);
            }

            if (request.Combinable is { } combinable)
            {
                promotion.SetCombinable(combinable, now);
            }

            if (request.Active is { } active)
            {
                if (active)
                {
                    promotion.Activate(now);
                }
                else
                {
                    promotion.Deactivate(now);
                }
            }

            // Storefront scope + active window are set together, only when the caller opts in (ApplyScope) —
            // any of the three may legitimately be null (all-storefront / open-ended), so a partial update
            // that omits them must not wipe them (mirrors the Offer update).
            if (request.ApplyScope)
            {
                promotion.SetStorefront(request.StorefrontId, now);
                promotion.SetActiveWindow(request.ActiveFrom, request.ActiveUntil, now);
            }

            await audit.RecordAsync(user.Mutation(
                promotion.TenantId, "Promotion", promotion.Id.ToString(), "catalog.promotion.update", promotion.Name), ct);
            // Publish before Save (see Create) so the PromotionChanged outbox row is committed and delivered.
            await publisher.Publish(ToEvent(promotion), ct);
            await db.SaveChangesAsync(ct);
            return TypedResults.Ok(ToDto(promotion));
        }
        catch (CatalogRuleException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    // Enums cross HTTP as numbers (platform invariant): the admin form posts 1/2, mapped onto the domain
    // enum here. An unknown value fails the domain's Enum.IsDefined guard as a 400, not a 500.
    private static PromotionScope ToScope(int scope) => (PromotionScope)scope;

    private static PromotionChanged ToEvent(Promotion p) =>
        new(p.Id, p.TenantId, p.StorefrontId, p.Name, p.Currency, (PromotionScopeKind)(int)p.Scope, p.ProductId,
            p.MinimumAmountMinor, p.MinimumQuantity, p.GrantsFreeShipping, p.PercentOff, p.DiscountAmountMinor,
            p.Combinable, p.IsActive, p.ActiveFrom, p.ActiveUntil,
            p.Code, p.MaxRedemptions, p.MaxRedemptionsPerCustomer);

    private static Guid DefaultTenantId(IConfiguration config) =>
        Guid.TryParse(config["Tenancy:DefaultTenantId"], out var tenantId)
            ? tenantId
            : new Guid("00000000-0000-0000-0000-000000000001");

    private static PromotionDto ToDto(Promotion p, string productTitle = "") =>
        new(p.Id, p.TenantId, p.StorefrontId, p.Name, p.Currency, (int)p.Scope, p.Scope.ToString(), p.ProductId,
            p.MinimumAmountMinor, p.MinimumQuantity, p.GrantsFreeShipping, p.PercentOff, p.DiscountAmountMinor,
            p.Combinable, p.Status.ToString(), p.IsActive, productTitle, p.ActiveFrom, p.ActiveUntil,
            p.Code, p.MaxRedemptions, p.MaxRedemptionsPerCustomer);
}

public record CreatePromotionRequest(
    Guid? TenantId,
    [property: Required, StringLength(120, MinimumLength = 1)] string Name,
    [property: Required, StringLength(3, MinimumLength = 3)] string Currency,
    // 1 = Storefront (whole cart), 2 = Product. Enums cross HTTP as numbers.
    [property: Range(1, 2)] int Scope,
    Guid? ProductId = null,
    Guid? StorefrontId = null,
    [property: Range(0, long.MaxValue)] long MinimumAmountMinor = 0,
    [property: Range(0, int.MaxValue)] int MinimumQuantity = 0,
    bool GrantsFreeShipping = false,
    [property: Range(0, 100)] int PercentOff = 0,
    [property: Range(0, long.MaxValue)] long DiscountAmountMinor = 0,
    bool Combinable = false,
    DateTimeOffset? ActiveFrom = null,
    DateTimeOffset? ActiveUntil = null,
    // Coupon codes (ADR-0052). Code set => the promotion applies only when the shopper enters it;
    // null/blank => automatic. Normalized to trimmed UPPERCASE and unique per tenant among non-null codes.
    // The two limits are null = unlimited; single-use is simply MaxRedemptions = 1.
    [property: StringLength(40)] string? Code = null,
    [property: Range(1, int.MaxValue)] int? MaxRedemptions = null,
    [property: Range(1, int.MaxValue)] int? MaxRedemptionsPerCustomer = null);

public record UpdatePromotionRequest(
    string? Name = null,
    [property: Range(0, long.MaxValue)] long? MinimumAmountMinor = null,
    [property: Range(0, int.MaxValue)] int? MinimumQuantity = null,
    bool? GrantsFreeShipping = null,
    [property: Range(0, 100)] int? PercentOff = null,
    [property: Range(0, long.MaxValue)] long? DiscountAmountMinor = null,
    bool? Combinable = null,
    bool? Active = null,
    // ApplyScope opts the update in to setting StorefrontId + the active window (any of which may be null).
    // A partial update that leaves it false keeps the promotion's current scope/window.
    bool ApplyScope = false,
    Guid? StorefrontId = null,
    DateTimeOffset? ActiveFrom = null,
    DateTimeOffset? ActiveUntil = null,
    // ApplyCode / ApplyUsageLimits opt the update in to setting the coupon code and the usage limits
    // (ADR-0052) — null is MEANINGFUL on all three (no code = automatic, no limit = unlimited), so a
    // partial update that omits the flag keeps the promotion's current values.
    bool ApplyCode = false,
    [property: StringLength(40)] string? Code = null,
    bool ApplyUsageLimits = false,
    [property: Range(1, int.MaxValue)] int? MaxRedemptions = null,
    [property: Range(1, int.MaxValue)] int? MaxRedemptionsPerCustomer = null);

public record PromotionDto(
    Guid Id, Guid TenantId, Guid? StorefrontId, string Name, string Currency, int Scope, string ScopeName,
    Guid? ProductId, long MinimumAmountMinor, int MinimumQuantity, bool GrantsFreeShipping, int PercentOff,
    long DiscountAmountMinor, bool Combinable, string Status, bool Active, string ProductTitle = "",
    DateTimeOffset? ActiveFrom = null, DateTimeOffset? ActiveUntil = null,
    // Coupon fields (ADR-0052), appended with defaults so every existing positional construction compiles.
    string? Code = null, int? MaxRedemptions = null, int? MaxRedemptionsPerCustomer = null);
