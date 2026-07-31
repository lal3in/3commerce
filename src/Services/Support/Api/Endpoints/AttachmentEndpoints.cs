using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.BuildingBlocks.Infrastructure.Storage;
using ThreeCommerce.Support.Domain;
using ThreeCommerce.Support.Infrastructure;

namespace ThreeCommerce.Support.Api.Endpoints;

/// <summary>
/// File attachments for support tickets and refund/return requests (mt6_9 object store): a customer
/// uploads a photo/PDF against a ticket or an RMA; the metadata lives in Support, the bytes in the
/// object store. Download is available to any authenticated user (customer who owns it, or an operator).
/// </summary>
public static class AttachmentEndpoints
{
    private static readonly Guid DefaultTenant = new("00000000-0000-0000-0000-000000000001");

    public static IEndpointRouteBuilder MapSupportAttachments(this IEndpointRouteBuilder app)
    {
        var customer = app.MapGroup("").WithTags("Support Attachments")
            .RequireAuthorization(InternalClaimsAuth.CustomerPolicy).DisableAntiforgery();
        customer.MapPost("/tickets/{ownerId:guid}/attachments", (Guid ownerId, IFormFile file, IObjectStore store, SupportDbContext db, TimeProvider time, CancellationToken ct)
            => Upload("ticket", ownerId, file, store, db, time, ct));
        customer.MapPost("/rma/{ownerId:guid}/attachments", (Guid ownerId, IFormFile file, IObjectStore store, SupportDbContext db, TimeProvider time, CancellationToken ct)
            => Upload("rma", ownerId, file, store, db, time, ct));
        customer.MapGet("/attachments/{id:guid}", Download);
        return app;
    }

    private static async Task<Results<Ok<AttachmentDto>, BadRequest<string>>> Upload(
        string ownerKind, Guid ownerId, IFormFile file, IObjectStore store, SupportDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (!UploadPolicy.ValidateAttachment(file.ContentType, file.Length, out var error))
        {
            return TypedResults.BadRequest(error);
        }

        var id = Guid.CreateVersion7();
        var key = StoredObjectKey.For(DefaultTenant, $"support-{ownerKind}", id.ToString(), file.FileName);
        await using (var content = file.OpenReadStream())
        {
            await store.PutAsync(key, content, file.ContentType, ct);
        }

        var attachment = new SupportAttachment
        {
            Id = id,
            OwnerKind = ownerKind,
            OwnerId = ownerId,
            FileName = Path.GetFileName(file.FileName),
            ContentType = file.ContentType,
            SizeBytes = file.Length,
            StorageKey = key,
            CreatedAt = time.GetUtcNow(),
        };
        db.Attachments.Add(attachment);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new AttachmentDto(attachment.Id, attachment.FileName, attachment.ContentType, attachment.SizeBytes));
    }

    private static async Task<Results<FileStreamHttpResult, NotFound>> Download(Guid id, IObjectStore store, SupportDbContext db, CancellationToken ct)
    {
        var attachment = await db.Attachments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct);
        if (attachment is null)
        {
            return TypedResults.NotFound();
        }

        var stream = await store.GetAsync(attachment.StorageKey, ct);
        return stream is null
            ? TypedResults.NotFound()
            : TypedResults.Stream(stream, attachment.ContentType, attachment.FileName);
    }
}

public record AttachmentDto(Guid Id, string FileName, string ContentType, long SizeBytes);
