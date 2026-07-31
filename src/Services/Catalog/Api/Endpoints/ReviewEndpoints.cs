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
        var reviews = await db.ProductReviews.AsNoTracking()
            .Where(r => r.ProductId == productId)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new ReviewDto(r.Id, r.AuthorName, r.Rating, r.Comment, r.CreatedAt))
            .ToListAsync(ct);
        var average = reviews.Count == 0 ? 0 : Math.Round(reviews.Average(r => r.Rating), 2);
        return TypedResults.Ok(new ReviewSummaryDto(productId, average, reviews.Count, reviews));
    }

    private static async Task<Results<Ok<ReviewDto>, ForbidHttpResult, BadRequest<string>, NotFound>> SubmitReview(
        Guid productId, SubmitReviewRequest request, ClaimsPrincipal user, CatalogDbContext db, TimeProvider time, CancellationToken ct)
    {
        // Verified-only: the gateway stamps email_verified into the internal claims (5-min freshness).
        if (!string.Equals(user.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return TypedResults.Forbid();
        }

        if (request.Rating is < 1 or > 5)
        {
            return TypedResults.BadRequest("Rating must be between 1 and 5.");
        }

        if (!await db.Products.AnyAsync(p => p.Id == productId, ct))
        {
            return TypedResults.NotFound();
        }

        var userId = Guid.Parse(user.FindFirstValue("sub")!);
        var now = time.GetUtcNow();
        var authorName = ResolveAuthorName(request.AuthorName, user.FindFirstValue("email"));
        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();

        // Upsert: one review per (product, user).
        var review = await db.ProductReviews.SingleOrDefaultAsync(r => r.ProductId == productId && r.UserId == userId, ct);
        if (review is null)
        {
            review = new ProductReview { Id = Guid.CreateVersion7(), ProductId = productId, UserId = userId, AuthorName = authorName, Rating = request.Rating, Comment = comment, CreatedAt = now };
            db.ProductReviews.Add(review);
        }
        else
        {
            review.Rating = request.Rating;
            review.Comment = comment;
            review.AuthorName = authorName;
            review.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new ReviewDto(review.Id, review.AuthorName, review.Rating, review.Comment, review.CreatedAt));
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

public record SubmitReviewRequest([property: Range(1, 5)] int Rating, string? Comment, string? AuthorName);
public record ReviewDto(Guid Id, string AuthorName, int Rating, string? Comment, DateTimeOffset CreatedAt);
public record ReviewSummaryDto(Guid ProductId, double Average, int Count, List<ReviewDto> Items);
public record AdminReviewDto(Guid Id, Guid ProductId, string ProductName, string ProductSlug, string AuthorName, int Rating, string? Comment, DateTimeOffset CreatedAt);
