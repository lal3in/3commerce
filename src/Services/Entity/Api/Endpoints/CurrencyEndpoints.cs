using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using MassTransit;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Reference;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Entity.Domain;
using ThreeCommerce.Entity.Infrastructure;

namespace ThreeCommerce.Entity.Api.Endpoints;

/// <summary>
/// Managed currency registry (currency_1): the tenant's supported currencies + their display metadata.
/// Entity owns it as reference/master data (ADR-0027); every mutation publishes <see cref="CurrencyChanged"/>
/// so services that validate/format money project it locally rather than reading this DB.
/// </summary>
public static class CurrencyEndpoints
{
    public static IEndpointRouteBuilder MapCurrencies(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/currencies").WithTags("Currencies")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);

        group.MapGet("/", List);
        group.MapPost("/", Create);
        group.MapPut("/{code}", Update);
        group.MapPost("/{code}/enable", Enable);
        group.MapPost("/{code}/disable", Disable);
        return app;
    }

    private static async Task<Ok<List<CurrencyDto>>> List(
        Guid? tenantId, bool? includeDisabled, EntityDbContext db, IConfiguration config, CancellationToken ct)
    {
        var tid = tenantId ?? DefaultTenantId(config);
        var query = db.Currencies.AsNoTracking().Where(c => c.TenantId == tid);
        if (includeDisabled != true)
        {
            query = query.Where(c => c.Enabled);
        }

        var rows = await query.OrderBy(c => c.Code).Select(c => ToDto(c)).ToListAsync(ct);
        return TypedResults.Ok(rows);
    }

    private static async Task<Results<Created<CurrencyDto>, BadRequest<string>, Conflict<string>>> Create(
        CreateCurrencyRequest request, EntityDbContext db, IPublishEndpoint publish, AuditRecorder audit,
        ClaimsPrincipal user, IConfiguration config, CancellationToken ct)
    {
        var tid = request.TenantId ?? DefaultTenantId(config);
        var code = (request.Code ?? string.Empty).Trim().ToUpperInvariant();
        if (await db.Currencies.AnyAsync(c => c.TenantId == tid && c.Code == code, ct))
        {
            return TypedResults.Conflict($"Currency {code} already exists.");
        }

        Currency currency;
        try
        {
            currency = Currency.Create(tid, code, request.Name ?? string.Empty, request.Symbol ?? string.Empty, request.DecimalPlaces, DateTimeOffset.UtcNow);
        }
        catch (DomainRuleException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }

        db.Currencies.Add(currency);
        await PublishAsync(publish, audit, user, currency, "create", ct);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/admin/currencies/{currency.Code}", ToDto(currency));
    }

    private static async Task<Results<Ok<CurrencyDto>, BadRequest<string>, NotFound>> Update(
        string code, UpdateCurrencyRequest request, EntityDbContext db, IPublishEndpoint publish, AuditRecorder audit,
        ClaimsPrincipal user, IConfiguration config, CancellationToken ct)
    {
        var currency = await FindAsync(db, code, request.TenantId, config, ct);
        if (currency is null)
        {
            return TypedResults.NotFound();
        }

        try
        {
            currency.UpdateDetails(request.Name, request.Symbol ?? string.Empty, request.DecimalPlaces, DateTimeOffset.UtcNow);
        }
        catch (DomainRuleException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }

        await PublishAsync(publish, audit, user, currency, "update", ct);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(currency));
    }

    private static Task<Results<Ok<CurrencyDto>, BadRequest<string>, NotFound>> Enable(
        string code, Guid? tenantId, EntityDbContext db, IPublishEndpoint publish, AuditRecorder audit,
        ClaimsPrincipal user, IConfiguration config, CancellationToken ct) =>
        ToggleAsync(code, tenantId, db, publish, audit, user, config, enable: true, ct);

    private static Task<Results<Ok<CurrencyDto>, BadRequest<string>, NotFound>> Disable(
        string code, Guid? tenantId, EntityDbContext db, IPublishEndpoint publish, AuditRecorder audit,
        ClaimsPrincipal user, IConfiguration config, CancellationToken ct) =>
        ToggleAsync(code, tenantId, db, publish, audit, user, config, enable: false, ct);

    private static async Task<Results<Ok<CurrencyDto>, BadRequest<string>, NotFound>> ToggleAsync(
        string code, Guid? tenantId, EntityDbContext db, IPublishEndpoint publish, AuditRecorder audit,
        ClaimsPrincipal user, IConfiguration config, bool enable, CancellationToken ct)
    {
        var currency = await FindAsync(db, code, tenantId, config, ct);
        if (currency is null)
        {
            return TypedResults.NotFound();
        }

        var now = DateTimeOffset.UtcNow;
        if (enable)
        {
            currency.Enable(now);
        }
        else
        {
            currency.Disable(now);
        }

        await PublishAsync(publish, audit, user, currency, enable ? "enable" : "disable", ct);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(currency));
    }

    private static Task<Currency?> FindAsync(EntityDbContext db, string code, Guid? tenantId, IConfiguration config, CancellationToken ct)
    {
        var tid = tenantId ?? DefaultTenantId(config);
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        return db.Currencies.SingleOrDefaultAsync(c => c.TenantId == tid && c.Code == normalized, ct);
    }

    private static async Task PublishAsync(
        IPublishEndpoint publish, AuditRecorder audit, ClaimsPrincipal user, Currency c, string action, CancellationToken ct)
    {
        await publish.Publish(new CurrencyChanged(c.TenantId, c.Code, c.Name, c.Symbol, c.DecimalPlaces, c.Enabled), ct);
        await audit.RecordAsync(user.Mutation(c.TenantId, "Currency", c.Code, $"entity.currency.{action}", c.Code), ct);
    }

    private static Guid DefaultTenantId(IConfiguration config) =>
        Guid.TryParse(config["Tenancy:DefaultTenantId"], out var tenantId)
            ? tenantId
            : new Guid("00000000-0000-0000-0000-000000000001");

    private static CurrencyDto ToDto(Currency c) =>
        new(c.Code, c.Name, c.Symbol, c.DecimalPlaces, c.Enabled, c.UpdatedAt);
}

public record CurrencyDto(string Code, string Name, string Symbol, int DecimalPlaces, bool Enabled, DateTimeOffset UpdatedAt);

public record CreateCurrencyRequest(
    Guid? TenantId,
    [property: Required, StringLength(3, MinimumLength = 3)] string Code,
    [property: Required] string Name,
    string? Symbol,
    [property: Range(0, 4)] int DecimalPlaces);

public record UpdateCurrencyRequest(
    Guid? TenantId,
    [property: Required] string Name,
    string? Symbol,
    [property: Range(0, 4)] int DecimalPlaces);
