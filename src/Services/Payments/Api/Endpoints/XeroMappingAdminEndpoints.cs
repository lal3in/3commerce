using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Payments.Domain.Xero;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.Payments.Api.Endpoints;

public static class XeroMappingAdminEndpoints
{
    public static IEndpointRouteBuilder MapXeroMappings(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/xero/mappings").WithTags("Admin Xero Mappings")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);
        group.MapGet("/", List);
        group.MapPost("/", Create);
        group.MapPut("/{id:guid}", Update);
        group.MapDelete("/{id:guid}", Delete);
        return app;
    }

    private static async Task<Ok<List<XeroAccountMappingDto>>> List(Guid tenantId, PaymentsDbContext db, CancellationToken ct)
    {
        var mappings = await db.XeroAccountMappings.AsNoTracking()
            .Where(m => m.TenantId == tenantId)
            .OrderBy(m => m.LedgerAccountCode)
            .ThenByDescending(m => m.Scope)
            .Select(m => ToDto(m))
            .ToListAsync(ct);
        return TypedResults.Ok(mappings);
    }

    private static async Task<Results<Created<XeroAccountMappingDto>, BadRequest<string>>> Create(
        UpsertXeroAccountMappingRequest request, PaymentsDbContext db, CancellationToken ct)
    {
        if (!Enum.IsDefined(request.Scope))
        {
            return TypedResults.BadRequest("Unsupported Xero mapping scope.");
        }

        var mapping = new XeroAccountMapping
        {
            Id = Guid.CreateVersion7(),
            TenantId = request.TenantId,
            Scope = request.Scope,
            StorefrontId = request.StorefrontId,
            CategoryId = request.CategoryId,
            SupplierEntityId = request.SupplierEntityId,
            ProductId = request.ProductId,
            LedgerAccountCode = request.LedgerAccountCode.Trim(),
            XeroAccountCode = request.XeroAccountCode.Trim(),
            Active = request.Active,
        };
        db.XeroAccountMappings.Add(mapping);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/admin/xero/mappings/{mapping.Id}", ToDto(mapping));
    }

    private static async Task<Results<Ok<XeroAccountMappingDto>, NotFound, BadRequest<string>>> Update(
        Guid id, UpsertXeroAccountMappingRequest request, PaymentsDbContext db, CancellationToken ct)
    {
        if (!Enum.IsDefined(request.Scope))
        {
            return TypedResults.BadRequest("Unsupported Xero mapping scope.");
        }

        var mapping = await db.XeroAccountMappings.SingleOrDefaultAsync(m => m.Id == id, ct);
        if (mapping is null)
        {
            return TypedResults.NotFound();
        }

        mapping.Scope = request.Scope;
        mapping.StorefrontId = request.StorefrontId;
        mapping.CategoryId = request.CategoryId;
        mapping.SupplierEntityId = request.SupplierEntityId;
        mapping.ProductId = request.ProductId;
        mapping.LedgerAccountCode = request.LedgerAccountCode.Trim();
        mapping.XeroAccountCode = request.XeroAccountCode.Trim();
        mapping.Active = request.Active;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(mapping));
    }

    private static async Task<Results<NoContent, NotFound>> Delete(Guid id, PaymentsDbContext db, CancellationToken ct)
    {
        var mapping = await db.XeroAccountMappings.SingleOrDefaultAsync(m => m.Id == id, ct);
        if (mapping is null)
        {
            return TypedResults.NotFound();
        }

        db.XeroAccountMappings.Remove(mapping);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static XeroAccountMappingDto ToDto(XeroAccountMapping m) => new(
        m.Id, m.TenantId, m.Scope.ToString(), m.StorefrontId, m.CategoryId, m.SupplierEntityId,
        m.ProductId, m.LedgerAccountCode, m.XeroAccountCode, m.Active);
}

public record UpsertXeroAccountMappingRequest(
    [property: Required] Guid TenantId,
    XeroMappingScope Scope,
    Guid? StorefrontId,
    Guid? CategoryId,
    Guid? SupplierEntityId,
    Guid? ProductId,
    [property: Required] string LedgerAccountCode,
    [property: Required] string XeroAccountCode,
    bool Active = true);

public record XeroAccountMappingDto(
    Guid Id, Guid TenantId, string Scope, Guid? StorefrontId, Guid? CategoryId, Guid? SupplierEntityId,
    Guid? ProductId, string LedgerAccountCode, string XeroAccountCode, bool Active);
