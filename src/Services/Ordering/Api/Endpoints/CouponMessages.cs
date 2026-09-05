using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Api.Endpoints;

/// <summary>
/// Turns a <see cref="CouponStatus"/> into the plain-English sentence a refused checkout returns
/// (ADR-0052), matching every other checkout 400 in this service.
/// <para>
/// The SHOPPER-facing localized wording lives in the storefront, keyed off the numeric status that
/// <c>GET /cart/summary</c> returns — the storefront speaks six languages and this service speaks none.
/// This text is the API's own diagnostic: it is what a non-first-party client, a support engineer reading
/// a log, or the browser sees when it posts a checkout the preview never validated.
/// </para>
/// </summary>
public static class CouponMessages
{
    /// <summary>The message for one refusal, naming the code the shopper actually typed.</summary>
    public static string For(CouponStatus status, string? code) => status switch
    {
        CouponStatus.UnknownCode => $"Coupon code '{code}' is not recognised.",
        CouponStatus.Inactive => $"Coupon code '{code}' is no longer active.",
        CouponStatus.NotStarted => $"Coupon code '{code}' is not available yet.",
        CouponStatus.Expired => $"Coupon code '{code}' has expired.",
        CouponStatus.WrongStorefront => $"Coupon code '{code}' cannot be used on this store.",
        CouponStatus.ThresholdNotMet => $"Your cart does not meet the conditions for coupon code '{code}'.",
        CouponStatus.UsageLimitReached => $"Coupon code '{code}' has reached its usage limit.",
        CouponStatus.CustomerLimitReached => $"You have already used coupon code '{code}'.",
        // Applied / None never reach here; a defensive default beats an exception on a money path.
        _ => $"Coupon code '{code}' could not be applied.",
    };
}
