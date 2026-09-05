namespace ThreeCommerce.Ordering.Domain;

/// <summary>
/// What happened to the coupon code the shopper entered (ADR-0052). Distinct reasons, not one blanket
/// "invalid coupon": a shopper who typed a real code that has simply expired, or whose cart is €5 short
/// of the minimum, must be told THAT — the storefront maps each member onto its own localized message.
/// <para>Crosses HTTP as a NUMBER (platform invariant), so members are never renumbered.</para>
/// </summary>
public enum CouponStatus
{
    /// <summary>No code was entered; nothing to report.</summary>
    None = 0,

    /// <summary>The code was accepted and its promotion is applied to this cart.</summary>
    Applied = 1,

    /// <summary>No promotion in this tenant carries that code.</summary>
    UnknownCode = 2,

    /// <summary>The promotion exists but has been switched off.</summary>
    Inactive = 3,

    /// <summary>The promotion's active window has not started yet.</summary>
    NotStarted = 4,

    /// <summary>The promotion's active window has ended.</summary>
    Expired = 5,

    /// <summary>The promotion belongs to a different storefront, or is denominated in another currency.</summary>
    WrongStorefront = 6,

    /// <summary>The cart does not meet the promotion's money and/or quantity threshold.</summary>
    ThresholdNotMet = 7,

    /// <summary>The promotion has been redeemed its maximum number of times.</summary>
    UsageLimitReached = 8,

    /// <summary>This customer has already redeemed the promotion as often as it allows.</summary>
    CustomerLimitReached = 9,
}

/// <summary>The outcome of validating one entered coupon code against one cart.</summary>
/// <param name="Status">Applied, or the single most specific reason it was refused.</param>
/// <param name="PromotionId">The promotion the code resolved to; null when the code is unknown.</param>
/// <param name="Name">The promotion's shopper-visible label; empty when the code is unknown.</param>
public readonly record struct CouponEvaluation(CouponStatus Status, Guid? PromotionId, string Name)
{
    /// <summary>Nothing was entered.</summary>
    public static CouponEvaluation None { get; } = new(CouponStatus.None, null, string.Empty);

    /// <summary>Whether the coupon's promotion should participate in this cart's evaluation.</summary>
    public bool IsApplied => Status == CouponStatus.Applied;
}

/// <summary>
/// Decides WHY a coupon code did or did not apply (ADR-0052). Pure — no EF, no clock, no counts of its
/// own: the caller supplies the promotion it looked up by code and the redemption counts it read, so the
/// same rules produce the same answer for <c>GET /cart/summary</c> (a preview) and for checkout (the
/// charge). Checkout's usage-limit answer is still made authoritative by the atomic reservation; this
/// class is what turns a refusal into a reason the shopper can act on.
/// </summary>
public static class CouponValidator
{
    /// <summary>
    /// The canonical form of a code as stored and compared: trimmed and UPPERCASE, or null when blank.
    /// Mirrors Catalog's normalization so the two sides can never disagree about what a code IS.
    /// </summary>
    public static string? Normalize(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();

    /// <summary>
    /// Evaluates an entered code against the cart. <paramref name="promotion"/> is the promotion found by
    /// code within the tenant — looked up WITHOUT the storefront/active/window filters, so a real code on
    /// the wrong store reports <see cref="CouponStatus.WrongStorefront"/> rather than pretending it does
    /// not exist. <paramref name="heldRedemptions"/> / <paramref name="customerHeldRedemptions"/> count
    /// Reserved + Confirmed redemptions; pass 0 when the promotion sets no matching limit.
    /// <para>
    /// Reasons are checked from the most structural to the most cart-dependent, so the shopper always
    /// gets the reason that is actually blocking them.
    /// </para>
    /// </summary>
    public static CouponEvaluation Evaluate(
        string? enteredCode,
        PromotionCopy? promotion,
        IReadOnlyList<PromotionLine> lines,
        Guid tenantId,
        Guid storefrontId,
        string currency,
        DateTimeOffset now,
        int heldRedemptions,
        int customerHeldRedemptions)
    {
        if (Normalize(enteredCode) is null)
        {
            return CouponEvaluation.None;
        }

        if (promotion is null || !promotion.IsCouponGated)
        {
            return new CouponEvaluation(CouponStatus.UnknownCode, null, string.Empty);
        }

        var id = promotion.PromotionId;
        var name = promotion.Name;

        if (!promotion.Active)
        {
            return new CouponEvaluation(CouponStatus.Inactive, id, name);
        }

        if (promotion.ActiveFrom is { } from && now < from)
        {
            return new CouponEvaluation(CouponStatus.NotStarted, id, name);
        }

        if (promotion.ActiveUntil is { } until && now > until)
        {
            return new CouponEvaluation(CouponStatus.Expired, id, name);
        }

        // Wrong store OR wrong currency: from the shopper's seat these are the same complaint — "this code
        // isn't for this shop" — and the no-FX invariant (ADR-0041) means a EUR coupon simply cannot apply
        // to an AUD cart, so it is reported as a scope problem rather than a mysterious no-op.
        if (promotion.TenantId != tenantId
            || (promotion.StorefrontId is { } scoped && scoped != storefrontId)
            || !string.Equals(promotion.Currency, currency, StringComparison.OrdinalIgnoreCase))
        {
            return new CouponEvaluation(CouponStatus.WrongStorefront, id, name);
        }

        // Usage limits before the threshold: an exhausted code is exhausted whatever the shopper adds to
        // the cart, so telling them to spend more would be a lie.
        if (promotion.MaxRedemptions is { } max && heldRedemptions >= max)
        {
            return new CouponEvaluation(CouponStatus.UsageLimitReached, id, name);
        }

        if (promotion.MaxRedemptionsPerCustomer is { } perCustomer && customerHeldRedemptions >= perCustomer)
        {
            return new CouponEvaluation(CouponStatus.CustomerLimitReached, id, name);
        }

        // Last: does THIS cart clear the promotion's own threshold and earn a reward? Delegated to the
        // shared evaluator so "does it apply" is decided by exactly the code that computes the discount.
        var candidate = PromotionEvaluator.CandidateFor(
            lines, id, promotion.Scope, promotion.ProductId, promotion.MinimumAmountMinor,
            promotion.MinimumQuantity, promotion.GrantsFreeShipping, promotion.PercentOff,
            promotion.DiscountAmountMinor, promotion.Combinable);
        return candidate is null
            ? new CouponEvaluation(CouponStatus.ThresholdNotMet, id, name)
            : new CouponEvaluation(CouponStatus.Applied, id, name);
    }
}
