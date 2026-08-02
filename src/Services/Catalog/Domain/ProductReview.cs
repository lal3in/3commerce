namespace ThreeCommerce.Catalog.Domain;

/// <summary>
/// A verified, signed-in customer's contribution on a product, of one of three shapes:
/// <list type="bullet">
///   <item><b>Review</b> — top-level (<see cref="ParentId"/> null) with a star <see cref="Rating"/>
///   (1–5). One per (product, user); re-submitting updates it. Only reviews feed the aggregate rating.</item>
///   <item><b>Comment</b> — top-level with no rating (<see cref="Rating"/> null). A member may add
///   several.</item>
///   <item><b>Reply</b> — <see cref="ParentId"/> points at a top-level review/comment; no rating. A
///   member may reply many times, including to other members' contributions.</item>
/// </list>
/// Readable by everyone on the storefront; admins can remove any (moderation — removing a parent also
/// removes its replies). <see cref="AuthorName"/> is a denormalized display name; the email is never stored.
/// </summary>
public class ProductReview
{
    public Guid Id { get; init; }
    public Guid ProductId { get; set; }
    public Guid UserId { get; set; }

    /// <summary>Null for a top-level review/comment; the parent's id for a reply (one level — replies
    /// always hang off a top-level entry, never off another reply).</summary>
    public Guid? ParentId { get; set; }

    public required string AuthorName { get; set; }

    /// <summary>1–5 for a review; null for a comment or a reply (they carry text, not a rating).</summary>
    public int? Rating { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
