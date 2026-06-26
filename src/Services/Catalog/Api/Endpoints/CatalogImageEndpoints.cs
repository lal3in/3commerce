using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.BuildingBlocks.Infrastructure.Storage;

namespace ThreeCommerce.Catalog.Api.Endpoints;

/// <summary>
/// Product image upload + serve (mt6_9 object store). Admin uploads a local file → it's validated
/// (allow-listed image types, 10 MB cap) and stored under a tenant-scoped key; the returned gateway URL
/// is what gets saved on the product. Serving is anonymous (product images are public).
/// </summary>
public static class CatalogImageEndpoints
{
    public static IEndpointRouteBuilder MapCatalogImages(this IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/images", Upload)
            .WithTags("Images").RequireAuthorization(InternalClaimsAuth.AdminPolicy).DisableAntiforgery();
        app.MapGet("/images/{**key}", Serve).WithTags("Images");
        return app;
    }

    private static async Task<Results<Ok<UploadedImage>, BadRequest<string>>> Upload(
        IFormFile file, IObjectStore store, ClaimsPrincipal user, IConfiguration config, CancellationToken ct)
    {
        if (!UploadPolicy.ValidateImage(file.ContentType, file.Length, out var error))
        {
            return TypedResults.BadRequest(error);
        }

        var key = StoredObjectKey.For(TenantOf(user, config), "product", Guid.CreateVersion7().ToString(), file.FileName);
        await using var content = file.OpenReadStream();
        await store.PutAsync(key, content, file.ContentType, ct);
        return TypedResults.Ok(new UploadedImage($"/api/catalog/images/{key}", key));
    }

    private static async Task<Results<FileStreamHttpResult, NotFound>> Serve(string key, IObjectStore store, CancellationToken ct)
    {
        var stream = await store.GetAsync(key, ct);
        return stream is null ? TypedResults.NotFound() : TypedResults.Stream(stream, ContentTypeFor(key));
    }

    private static Guid TenantOf(ClaimsPrincipal user, IConfiguration config)
    {
        if (Guid.TryParse(user.FindFirst("tenant")?.Value, out var tenant))
        {
            return tenant;
        }

        return Guid.TryParse(config["Tenancy:DefaultTenantId"], out var fallback) ? fallback : new Guid("00000000-0000-0000-0000-000000000001");
    }

    private static string ContentTypeFor(string key) => Path.GetExtension(key).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "application/octet-stream",
    };
}

public record UploadedImage(string Url, string Key);
