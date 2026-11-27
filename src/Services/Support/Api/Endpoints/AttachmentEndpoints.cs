using System.Security.Claims;
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
/// object store. Upload is owner-only and download is owner-or-operator (ADR-0018 amendment 2026-11-27): the owner is
/// whoever placed the order the ticket / RMA belongs to; anyone else gets 404.
/// </summary>
public static class AttachmentEndpoints
{
    private static readonly Guid DefaultTenant = new("00000000-0000-0000-0000-000000000001");

    public static IEndpointRouteBuilder MapSupportAttachments(this IEndpointRouteBuilder app)
    {
        var customer = app.MapGroup("").WithTags("Support Attachments")
            .RequireAuthorization(InternalClaimsAuth.CustomerPolicy).DisableAntiforgery();
        customer.MapPost("/tickets/{ownerId:guid}/attachments", (Guid ownerId, IFormFile file, ClaimsPrincipal user, IObjectStore store, SupportDbContext db, TimeProvider time, CancellationToken ct)
            => Upload("ticket", ownerId, file, user, store, db, time, ct));
        customer.MapPost("/rma/{ownerId:guid}/attachments", (Guid ownerId, IFormFile file, ClaimsPrincipal user, IObjectStore store, SupportDbContext db, TimeProvider time, CancellationToken ct)
            => Upload("rma", ownerId, file, user, store, db, time, ct));
        customer.MapGet("/attachments/{id:guid}", Download);
        return app;
    }

    private static async Task<Results<Ok<AttachmentDto>, BadRequest<string>, NotFound>> Upload(
        string ownerKind, Guid ownerId, IFormFile file, ClaimsPrincipal user, IObjectStore store, SupportDbContext db, TimeProvider time, CancellationToken ct)
    {
        // Only onto the caller's own ticket / RMA — one that exists, on an order they placed.
        if (await OrderOwnership.OrderOfAsync(db, ownerKind, ownerId, ct) is not { } orderId
            || !await OrderOwnership.OwnsOrderAsync(db, orderId, user, ct))
        {
            return TypedResults.NotFound();
        }

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

    private static async Task<Results<FileStreamHttpResult, NotFound>> Download(
        Guid id, ClaimsPrincipal user, IObjectStore store, SupportDbContext db, CancellationToken ct)
    {
        var attachment = await db.Attachments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct);
        if (attachment is null)
        {
            return TypedResults.NotFound();
        }

        // An operator (the Admin console reads attachments through this route) or the owner of the ticket / RMA's order.
        if (!OrderOwnership.IsOperator(user)
            && (await OrderOwnership.OrderOfAsync(db, attachment.OwnerKind, attachment.OwnerId, ct) is not { } orderId
                || !await OrderOwnership.OwnsOrderAsync(db, orderId, user, ct)))
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
