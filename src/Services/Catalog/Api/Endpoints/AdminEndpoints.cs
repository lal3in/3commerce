using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Catalog.Domain;
using ThreeCommerce.Catalog.Infrastructure;

namespace ThreeCommerce.Catalog.Api.Endpoints;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdmin(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin")
            .WithTags("Admin")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);

        group.MapGet("/import-runs", ListImportRuns);
        group.MapPost("/import-runs", TriggerImport);
        group.MapDelete("/products/{id:guid}", DeleteProduct);

        return app;
    }

    private static async Task<Ok<List<ImportRunResponse>>> ListImportRuns(
        CatalogDbContext db, CancellationToken cancellationToken)
    {
        var runs = await db.ImportRuns.AsNoTracking()
            .OrderByDescending(r => r.StartedAt)
            .Take(50)
            .Select(r => new ImportRunResponse(
                r.Id, r.Importer, r.StartedAt, r.CompletedAt, r.RowsRead, r.Accepted, r.Rejected, r.SampleRejections))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(runs);
    }

    /// <summary>
    /// Synchronous in v1 (seconds-to-minutes for 10k rows) — acceptable for an
    /// admin-only endpoint; goes async behind the bus when real feeds arrive.
    /// </summary>
    private static async Task<Created<ImportRunResponse>> TriggerImport(
        ISupplierImporter importer, CancellationToken cancellationToken)
    {
        var result = await importer.RunAsync(cancellationToken);
        return TypedResults.Created($"/admin/import-runs/{result.RunId}", new ImportRunResponse(
            result.RunId, importer.Name, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            result.RowsRead, result.Accepted, result.Rejected, result.SampleRejections.ToList()));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteProduct(
        Guid id, CatalogDbContext db, CancellationToken cancellationToken)
    {
        var product = await db.Products.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (product is null)
        {
            return TypedResults.NotFound();
        }

        db.Products.Remove(product);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}

public record ImportRunResponse(
    Guid Id,
    string Importer,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    int RowsRead,
    int Accepted,
    int Rejected,
    IReadOnlyList<string> SampleRejections);
