using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Catalog.Domain;
using ThreeCommerce.Catalog.Infrastructure;

namespace ThreeCommerce.Catalog.Api.Endpoints;

/// <summary>
/// Product ratings &amp; reviews. Anyone can read a product's reviews and its aggregate rating; only a
/// signed-in, email-verified customer can leave one (one per product — re-posting updates it); only an
/// admin can remove one (moderation).
/// </summary>
public static class ReviewEndpoints
{
    public static IEndpointRouteBuilder MapReviews(this IEndpointRouteBuilder app)
    {
        // Public read — no auth (everyone, including guests, sees ratings & reviews).
        app.MapGet("/products/{productId:guid}/reviews", ListForProduct).WithTags("Reviews");

        // Verified-customer write.
        app.MapPost("/products/{productId:guid}/reviews", SubmitReview).WithTags("Reviews")
            .RequireAuthorization(InternalClaimsAuth.CustomerPolicy);

        // Admin moderation.
        var admin = app.MapGroup("/admin/reviews").WithTags("Admin Reviews")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);
        admin.MapGet("/", ListRecent).WithSummary("Recent reviews across products (moderation).");
        admin.MapDelete("/{id:guid}", DeleteReview).WithSummary("Remove a review.");
        return app;
    }

    private static async Task<Ok<ReviewSummaryDto>> ListForProduct(Guid productId, CatalogDbContext db, CancellationToken ct)
    {
        var all = await db.ProductReviews.AsNoTracking()
            .Where(r => r.ProductId == productId)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(ct);

        // Replies grouped under their parent; top-level entries (reviews + comments) newest-first, each
        // carrying its replies oldest-first (a conversation reads top-to-bottom).
        var repliesByParent = all.Where(r => r.ParentId is not null)
            .GroupBy(r => r.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(ToReplyDto).ToList());

        var topLevel = all.Where(r => r.ParentId is null)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new ReviewDto(r.Id, r.AuthorName, r.Rating, r.Comment, r.CreatedAt,
                repliesByParent.TryGetValue(r.Id, out var replies) ? replies : []))
            .ToList();

        // Only rating reviews feed the aggregate; comments/replies (no rating) are excluded.
        var rated = all.Where(r => r.ParentId is null && r.Rating is not null).Select(r => r.Rating!.Value).ToList();
        var average = rated.Count == 0 ? 0 : Math.Round(rated.Average(), 2);
        return TypedResults.Ok(new ReviewSummaryDto(productId, average, rated.Count, topLevel));
    }

    private static ReplyDto ToReplyDto(ProductReview r) => new(r.Id, r.AuthorName, r.Comment ?? string.Empty, r.CreatedAt);

    private static async Task<Results<Ok<ReviewDto>, ForbidHttpResult, BadRequest<string>, NotFound>> SubmitReview(
        Guid productId, SubmitReviewRequest request, ClaimsPrincipal user, CatalogDbContext db, TimeProvider time, CancellationToken ct)
    {
        // Verified-only: the gateway stamps email_verified into the internal claims (5-min freshness).
        if (!string.Equals(user.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return TypedResults.Forbid();
        }

        if (!await db.Products.AnyAsync(p => p.Id == productId, ct))
        {
            return TypedResults.NotFound();
        }

        var userId = Guid.Parse(user.FindFirstValue("sub")!);
        var now = time.GetUtcNow();
        var authorName = ResolveAuthorName(request.AuthorName, user.FindFirstValue("email"));
        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();

        // A REPLY (ParentId set): text-only, no rating, one level deep, on the same product. A member may
        // reply many times, including to another member's contribution — always an insert.
        if (request.ParentId is { } parentId)
        {
            var parent = await db.ProductReviews.SingleOrDefaultAsync(r => r.Id == parentId && r.ProductId == productId, ct);
            if (parent is null)
            {
                return TypedResults.NotFound();
            }

            if (parent.ParentId is not null)
            {
                return TypedResults.BadRequest("Replies are one level deep — reply to the original review or comment.");
            }

            if (comment is null)
            {
                return TypedResults.BadRequest("A reply needs a message.");
            }

            var reply = new ProductReview { Id = Guid.CreateVersion7(), ProductId = productId, UserId = userId, ParentId = parentId, AuthorName = authorName, Rating = null, Comment = comment, CreatedAt = now };
            db.ProductReviews.Add(reply);
            await db.SaveChangesAsync(ct);
            return TypedResults.Ok(new ReviewDto(reply.Id, reply.AuthorName, null, reply.Comment, reply.CreatedAt, []));
        }

        // A top-level contribution. With a rating (1–5) it is a REVIEW (one per product/user — a re-submit
        // updates it); with no rating it is a COMMENT (text required; a member may add several).
        if (request.Rating is { } rating)
        {
            if (rating is < 1 or > 5)
            {
                return TypedResults.BadRequest("Rating must be between 1 and 5.");
            }

            var review = await db.ProductReviews.SingleOrDefaultAsync(r => r.ProductId == productId && r.UserId == userId && r.ParentId == null && r.Rating != null, ct);
            if (review is null)
            {
                review = new ProductReview { Id = Guid.CreateVersion7(), ProductId = productId, UserId = userId, AuthorName = authorName, Rating = rating, Comment = comment, CreatedAt = now };
                db.ProductReviews.Add(review);
            }
            else
            {
                review.Rating = rating;
                review.Comment = comment;
                review.AuthorName = authorName;
                review.UpdatedAt = now;
            }

            await db.SaveChangesAsync(ct);
            return TypedResults.Ok(new ReviewDto(review.Id, review.AuthorName, review.Rating, review.Comment, review.CreatedAt, []));
        }

        if (comment is null)
        {
            return TypedResults.BadRequest("A comment needs a message (or include a rating to leave a review).");
        }

        var newComment = new ProductReview { Id = Guid.CreateVersion7(), ProductId = productId, UserId = userId, AuthorName = authorName, Rating = null, Comment = comment, CreatedAt = now };
        db.ProductReviews.Add(newComment);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new ReviewDto(newComment.Id, newComment.AuthorName, null, newComment.Comment, newComment.CreatedAt, []));
    }

    private static async Task<Ok<List<AdminReviewDto>>> ListRecent(CatalogDbContext db, CancellationToken ct)
    {
        var reviews = await db.ProductReviews.AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .Take(200)
            .Join(db.Products.AsNoTracking(), r => r.ProductId, p => p.Id, (r, p) => new AdminReviewDto(
                r.Id, r.ProductId, p.Title, p.Slug, r.AuthorName, r.Rating, r.Comment, r.CreatedAt))
            .ToListAsync(ct);
        return TypedResults.Ok(reviews);
    }

    private static async Task<Results<NoContent, NotFound>> DeleteReview(Guid id, CatalogDbContext db, CancellationToken ct)
    {
        var review = await db.ProductReviews.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (review is null)
        {
            return TypedResults.NotFound();
        }

        // Removing a top-level review/comment also removes its replies (no orphaned threads). Removing a
        // reply just removes that reply.
        if (review.ParentId is null)
        {
            var replies = await db.ProductReviews.Where(r => r.ParentId == id).ToListAsync(ct);
            db.ProductReviews.RemoveRange(replies);
        }

        db.ProductReviews.Remove(review);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    // Public display name: prefer what the shopper supplies (their own name), else the email local-part,
    // never the full email. Capped to the column length.
    private static string ResolveAuthorName(string? supplied, string? email)
    {
        var name = supplied?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            var at = email?.IndexOf('@') ?? -1;
            name = at > 0 ? email![..at] : "Customer";
        }

        return name.Length > 60 ? name[..60] : name;
    }
}

// Rating present (1–5) → a review; Rating null + no ParentId → a product comment; ParentId set → a reply.
public record SubmitReviewRequest(int? Rating, string? Comment, string? AuthorName, Guid? ParentId = null);
public record ReplyDto(Guid Id, string AuthorName, string Message, DateTimeOffset CreatedAt);
public record ReviewDto(Guid Id, string AuthorName, int? Rating, string? Comment, DateTimeOffset CreatedAt, IReadOnlyList<ReplyDto> Replies);
public record ReviewSummaryDto(Guid ProductId, double Average, int Count, List<ReviewDto> Items);
public record AdminReviewDto(Guid Id, Guid ProductId, string ProductName, string ProductSlug, string AuthorName, int? Rating, string? Comment, DateTimeOffset CreatedAt);
