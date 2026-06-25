using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Fulfillment.Infrastructure;

namespace ThreeCommerce.Fulfillment.Api.Endpoints;

/// <summary>Carrier integration config + lifecycle (mt4_3). Tenant default + per-storefront overrides.</summary>
public static class CarrierEndpoints
{
    public static IEndpointRouteBuilder MapCarriers(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/carriers").WithTags("Carriers")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);

        group.MapPost("/", Configure);
        group.MapGet("/", List);
        group.MapPost("/{id:guid}/activate", (Guid id, Guid? tenantId, CarrierService svc, IConfiguration cfg, CancellationToken ct) =>
            Transition(id, tenantId, cfg, svc, (c, now) => c.Activate(now), ct));
        group.MapPost("/{id:guid}/suspend", (Guid id, Guid? tenantId, CarrierService svc, IConfiguration cfg, CancellationToken ct) =>
            Transition(id, tenantId, cfg, svc, (c, now) => c.Suspend(now), ct));
        group.MapPost("/{id:guid}/disable", (Guid id, Guid? tenantId, CarrierService svc, IConfiguration cfg, CancellationToken ct) =>
            Transition(id, tenantId, cfg, svc, (c, now) => c.Disable(now), ct));
        group.MapPost("/{id:guid}/default", MakeDefault);
        group.MapPut("/{id:guid}/credential", SetCredential);
        return app;
    }

    private static async Task<Results<Created<CarrierDto>, BadRequest<string>>> Configure(
        ConfigureCarrierRequest request, CarrierService svc, IConfiguration config, CancellationToken ct)
    {
        try
        {
            var integration = await svc.ConfigureAsync(
                request.TenantId ?? DefaultTenantId(config), request.StorefrontId, request.Carrier, request.CredentialRef, ct);
            return TypedResults.Created($"/admin/carriers/{integration.Id}", ToDto(integration));
        }
        catch (FulfillmentRuleException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private static async Task<Ok<List<CarrierDto>>> List(
        Guid? tenantId, Guid? storefrontId, CarrierService svc, IConfiguration config, CancellationToken ct)
    {
        var list = await svc.ListAsync(tenantId ?? DefaultTenantId(config), storefrontId, ct);
        return TypedResults.Ok(list.Select(ToDto).ToList());
    }

    private static async Task<Results<Ok<CarrierDto>, NotFound, BadRequest<string>>> Transition(
        Guid id, Guid? tenantId, IConfiguration config, CarrierService svc,
        Action<CarrierIntegration, DateTimeOffset> transition, CancellationToken ct)
    {
        try
        {
            var integration = await svc.TransitionAsync(tenantId ?? DefaultTenantId(config), id, transition, ct);
            return integration is null ? TypedResults.NotFound() : TypedResults.Ok(ToDto(integration));
        }
        catch (FulfillmentRuleException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private static async Task<Results<Ok<CarrierDto>, NotFound, BadRequest<string>>> MakeDefault(
        Guid id, Guid? tenantId, CarrierService svc, IConfiguration config, CancellationToken ct)
    {
        try
        {
            var integration = await svc.MakeDefaultAsync(tenantId ?? DefaultTenantId(config), id, ct);
            return integration is null ? TypedResults.NotFound() : TypedResults.Ok(ToDto(integration));
        }
        catch (FulfillmentRuleException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private static async Task<Results<Ok<CarrierDto>, NotFound>> SetCredential(
        Guid id, SetCredentialRequest request, Guid? tenantId, CarrierService svc, IConfiguration config, CancellationToken ct)
    {
        var integration = await svc.TransitionAsync(
            tenantId ?? DefaultTenantId(config), id, (c, now) => c.SetCredentialRef(request.CredentialRef, now), ct);
        return integration is null ? TypedResults.NotFound() : TypedResults.Ok(ToDto(integration));
    }

    private static Guid DefaultTenantId(IConfiguration config) =>
        Guid.TryParse(config["Tenancy:DefaultTenantId"], out var tenantId)
            ? tenantId
            : new Guid("00000000-0000-0000-0000-000000000001");

    private static CarrierDto ToDto(CarrierIntegration c) =>
        new(c.Id, c.TenantId, c.StorefrontId, c.Carrier.ToString(), c.Status.ToString(), c.IsDefault, c.CredentialRef is not null);
}

public record ConfigureCarrierRequest(Guid? TenantId, Guid? StorefrontId, CarrierCode Carrier, string? CredentialRef);

public record SetCredentialRequest([property: MaxLength(200)] string? CredentialRef);

public record CarrierDto(Guid Id, Guid TenantId, Guid? StorefrontId, string Carrier, string Status, bool IsDefault, bool HasCredential);
