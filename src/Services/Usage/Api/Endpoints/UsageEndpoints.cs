using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Usage.Domain;
using ThreeCommerce.Usage.Infrastructure;

namespace ThreeCommerce.Usage.Api.Endpoints;

/// <summary>Metered usage (mt7_4): provision an allowance, record usage, read balances.</summary>
public static class UsageEndpoints
{
    public static IEndpointRouteBuilder MapUsage(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/usage").WithTags("Usage")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);

        group.MapPost("/provision", Provision);
        group.MapPost("/record", Record);
        group.MapPost("/balances/{id:guid}/bill-overage", BillOverage);
        group.MapGet("/balances", Balances);

        // Customer "my usage" (mt7_6): scoped to the signed-in customer's tenant + email claims.
        app.MapGet("/me/usage", Mine).WithTags("Usage")
            .RequireAuthorization(InternalClaimsAuth.CustomerPolicy);
        return app;
    }

    private static async Task<Results<Ok<List<BalanceDto>>, UnauthorizedHttpResult>> Mine(
        ClaimsPrincipal user, UsageService usage, CancellationToken ct)
    {
        if (!CustomerClaims.TryRead(user, out var tenantId, out var email))
        {
            return TypedResults.Unauthorized();
        }

        var list = await usage.ListBalancesAsync(tenantId, email, ct);
        return TypedResults.Ok(list.Select(ToDto).ToList());
    }

    private static async Task<Results<Ok<BalanceDto>, BadRequest<string>>> Provision(
        ProvisionRequest request, UsageService usage, IConfiguration config, CancellationToken ct)
    {
        try
        {
            var balance = await usage.ProvisionAsync(
                request.TenantId ?? DefaultTenantId(config), request.CustomerEmail, request.Meter, request.IncludedQuantity,
                request.OverageAllowed, request.OverageUnitPriceMinor, request.Currency, request.PeriodEnd, ct);
            return TypedResults.Ok(ToDto(balance));
        }
        catch (UsageRuleException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private static async Task<Results<Ok<BalanceDto>, NotFound>> BillOverage(
        Guid id, Guid? tenantId, UsageService usage, IConfiguration config, CancellationToken ct)
    {
        var balance = await usage.BillOverageAsync(tenantId ?? DefaultTenantId(config), id, ct);
        return balance is null ? TypedResults.NotFound() : TypedResults.Ok(ToDto(balance));
    }

    private static async Task<Results<Ok<BalanceDto>, BadRequest<string>>> Record(
        RecordUsageRequest request, UsageService usage, IConfiguration config, CancellationToken ct)
    {
        try
        {
            var balance = await usage.RecordAsync(
                request.TenantId ?? DefaultTenantId(config), request.CustomerEmail, request.Meter, request.Quantity, request.ReferenceId, ct);
            return TypedResults.Ok(ToDto(balance));
        }
        catch (UsageRuleException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private static async Task<Ok<List<BalanceDto>>> Balances(
        Guid? tenantId, string? email, UsageService usage, IConfiguration config, CancellationToken ct)
    {
        var list = await usage.ListBalancesAsync(tenantId ?? DefaultTenantId(config), email, ct);
        return TypedResults.Ok(list.Select(ToDto).ToList());
    }

    private static Guid DefaultTenantId(IConfiguration config) =>
        Guid.TryParse(config["Tenancy:DefaultTenantId"], out var tenantId)
            ? tenantId
            : new Guid("00000000-0000-0000-0000-000000000001");

    private static BalanceDto ToDto(UsageBalance b) =>
        new(b.Id, b.CustomerEmail, b.Meter.ToString(), b.IncludedQuantity, b.UsedQuantity, b.RemainingQuantity,
            b.OverageQuantity, b.OverageAllowed, b.OverageUnitPriceMinor, b.UnbilledOverageChargeMinor, b.Currency, b.PeriodStart, b.PeriodEnd);
}

public record ProvisionRequest(
    Guid? TenantId, [property: Required, EmailAddress] string CustomerEmail, MeterType Meter,
    [property: Range(0, long.MaxValue)] long IncludedQuantity, bool OverageAllowed,
    [property: Range(0, long.MaxValue)] long OverageUnitPriceMinor, string Currency, DateTimeOffset? PeriodEnd);

public record RecordUsageRequest(
    Guid? TenantId, [property: Required, EmailAddress] string CustomerEmail, MeterType Meter,
    [property: Range(1, long.MaxValue)] long Quantity, string? ReferenceId);

public record BalanceDto(
    Guid Id, string CustomerEmail, string Meter, long IncludedQuantity, long UsedQuantity, long RemainingQuantity,
    long OverageQuantity, bool OverageAllowed, long OverageUnitPriceMinor, long UnbilledOverageChargeMinor, string Currency,
    DateTimeOffset PeriodStart, DateTimeOffset? PeriodEnd);
