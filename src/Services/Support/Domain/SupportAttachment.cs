namespace ThreeCommerce.Support.Domain;

/// <summary>
/// A file a customer attached to a support ticket or a refund/return request (e.g. a photo of a
/// damaged item, a receipt). Metadata lives here; the bytes live in the object store under
/// <see cref="StorageKey"/> (mt6_9). <see cref="OwnerKind"/> is "ticket" or "rma".
/// </summary>
public class SupportAttachment
{
    public Guid Id { get; init; }
    public required string OwnerKind { get; init; }
    public Guid OwnerId { get; init; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long SizeBytes { get; set; }
    public required string StorageKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
