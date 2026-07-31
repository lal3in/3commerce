namespace ThreeCommerce.Catalog.Domain;

/// <summary>
/// A customer's star rating (1–5) and optional written review of a product. Written only by a
/// verified, signed-in customer — one per product, re-submitting updates it — and readable by
/// everyone on the storefront. Admins can remove a review (moderation). <see cref="AuthorName"/>
/// is a denormalized display name; the customer's email is never stored here.
/// </summary>
public class ProductReview
{
    public Guid Id { get; init; }
    public Guid ProductId { get; set; }
    public Guid UserId { get; set; }
    public required string AuthorName { get; set; }
    public int Rating { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
