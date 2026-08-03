using Microsoft.AspNetCore.Http.HttpResults;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Catalog.Domain;
using ThreeCommerce.Catalog.Infrastructure;

namespace ThreeCommerce.Catalog.Api.Endpoints;

/// <summary>
/// Admin management of the per-tenant product-type shipping policy (mt4_3): which
/// <see cref="ProductType"/> values require shipping / a carrier. Drives the publish-readiness
/// shipping-dimension gate. The ProductType set itself is a fixed vocabulary (the enum).
/// </summary>
public static class ProductTypeEndpoints
{
    public static IEndpointRouteBuilder MapProductTypes(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/product-types").WithTags("Admin Product Types")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);

        group.MapGet("/", List);
        group.MapPut("/", Update);
        return app;
    }

    private static async Task<Ok<List<ProductTypePolicyRow>>> List(
        Guid tenantId, ProductTypeShippingPolicyService svc, CancellationToken ct)
    {
        var policy = await svc.GetOrDefaultAsync(tenantId, ct);
        var rows = Enum.GetValues<ProductType>()
            .Select(t => new ProductTypePolicyRow(t.ToString(), (int)t, policy.RequiresShipping(t)))
            .ToList();
        return TypedResults.Ok(rows);
    }

    private static async Task<Results<Ok<List<ProductTypePolicyRow>>, BadRequest<string>>> Update(
        UpdateProductTypePolicyRequest request, ProductTypeShippingPolicyService svc, CancellationToken ct)
    {
        var types = new List<ProductType>();
        foreach (var name in request.RequiresShippingTypes ?? [])
        {
            if (!Enum.TryParse<ProductType>(name, out var type))
            {
                return TypedResults.BadRequest($"Unknown product type '{name}'.");
            }

            types.Add(type);
        }

        var policy = await svc.SetAsync(request.TenantId, types, ct);
        var rows = Enum.GetValues<ProductType>()
            .Select(t => new ProductTypePolicyRow(t.ToString(), (int)t, policy.RequiresShipping(t)))
            .ToList();
        return TypedResults.Ok(rows);
    }
}

public sealed record ProductTypePolicyRow(string ProductType, int Value, bool RequiresShipping);

public sealed record UpdateProductTypePolicyRequest(Guid TenantId, string[]? RequiresShippingTypes);
