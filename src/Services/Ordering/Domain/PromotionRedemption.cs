namespace ThreeCommerce.Ordering.Domain;

/// <summary>Where a coupon redemption is in its lifecycle (ADR-0052).</summary>
public enum PromotionRedemptionStatus
{
    /// <summary>Held at checkout, before the payment settles. Counts against both usage limits.</summary>
    Reserved = 1,

    /// <summary>The order confirmed; the redemption is spent for good.</summary>
    Confirmed = 2,

    /// <summary>The checkout was cancelled or the payment failed; the hold was given back.</summary>
    Released = 3,
}

/// <summary>
/// One shopper's use of a coupon-gated promotion (ADR-0052), reserved AT CHECKOUT — not at confirmation.
/// The charged amount and the payment authorization are both fixed at checkout, so the coupon has to be
/// locked in there: a discount we cannot honour must never reach the payment provider.
/// <para>
/// <b>Lifecycle.</b> Checkout reserves (Reserved) → order confirmation confirms (Confirmed) → checkout
/// cancellation, payment failure or the saga's expiry timeout releases (Released). Confirm and release
/// are both status-guarded, so a redelivered message is a no-op.
/// </para>
/// <para>
/// <b>Keys.</b> <see cref="OrderId"/> is the id shared by the CheckoutAttempt and the Order it becomes
/// (checkout mints ONE id for both), so the same column serves as the checkout-attempt key and the order
/// key. A unique index on (PromotionId, OrderId) is what makes reservation idempotent — a retried
/// checkout for the same order can never take a second redemption.
/// </para>
/// </summary>
public class PromotionRedemption
{
    /// <summary>Identity (UUIDv7).</summary>
    public Guid Id { get; init; }

    /// <summary>The promotion whose allowance this consumes.</summary>
    public Guid PromotionId { get; init; }

    /// <summary>Owning tenant; a redemption never crosses tenants.</summary>
    public Guid TenantId { get; init; }

    /// <summary>The checkout attempt / order this redemption belongs to (one id for both).</summary>
    public Guid OrderId { get; init; }

    /// <summary>
    /// Who redeemed, for the per-customer limit: the authenticated user id (<c>u:{guid}</c>) when the
    /// shopper is signed in, else the normalized checkout email (<c>e:{lowercased}</c>) so a guest
    /// checkout still counts. Prefixed so the two namespaces can never collide.
    /// </summary>
    public required string CustomerKey { get; init; }

    /// <summary>The coupon code as stored on the promotion (canonical UPPERCASE) — kept for support.</summary>
    public required string Code { get; init; }

    /// <summary>Lifecycle status.</summary>
    public PromotionRedemptionStatus Status { get; set; } = PromotionRedemptionStatus.Reserved;

    /// <summary>When the hold was taken (UTC).</summary>
    public DateTimeOffset ReservedAt { get; init; }

    /// <summary>When the order confirmed (UTC); null until then.</summary>
    public DateTimeOffset? ConfirmedAt { get; set; }

    /// <summary>When the hold was given back (UTC); null unless released.</summary>
    public DateTimeOffset? ReleasedAt { get; set; }

    /// <summary>Whether this redemption still counts against the promotion's usage limits.</summary>
    public bool IsHeld => Status is PromotionRedemptionStatus.Reserved or PromotionRedemptionStatus.Confirmed;

    /// <summary>
    /// The per-customer identity used for <see cref="CustomerKey"/>: the signed-in user when there is one,
    /// otherwise the trimmed, lowercased checkout email, so a guest cannot reset a per-customer limit by
    /// staying signed out. Returns null only when neither is available (no limit can then be enforced).
    /// </summary>
    public static string? CustomerKeyFor(Guid? userId, string? email)
    {
        if (userId is { } id && id != Guid.Empty)
        {
            return $"u:{id}";
        }

        return string.IsNullOrWhiteSpace(email) ? null : $"e:{email.Trim().ToLowerInvariant()}";
    }
}
