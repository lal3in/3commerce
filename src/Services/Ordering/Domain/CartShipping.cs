using ThreeCommerce.BuildingBlocks.Contracts.Supply;

namespace ThreeCommerce.Ordering.Domain;

/// <summary>
/// Whether a cart pays shipping at all, decided from the same read copies checkout uses (ADR-0028/0050).
/// It lives here rather than inside an endpoint because BOTH the charge (<c>POST /checkout</c>) and the
/// preview (<c>GET /cart/summary</c>) must answer it identically: a cart that pays no shipping makes a
/// free-shipping promotion worth exactly 0, which is what lets the preview settle the promotion contest
/// deterministically instead of guessing a rate (ADR-0051).
/// </summary>
public static class CartShipping
{
    /// <summary>
    /// Does this cart line ship? The tenant's ProductType policy decides when we know the line's product
    /// type and a policy copy exists; otherwise we fall back to the fulfilment-type gate (the behaviour
    /// before the policy existed, and the answer for a line with no matching offer — default shippable).
    /// </summary>
    public static bool LineRequiresShipping(OfferCopy? offer, ProductTypeShippingPolicyCopy? policy)
    {
        if (offer is null)
        {
            return FulfilmentType.Unassigned.RequiresShipping();
        }

        if (policy is not null && offer.ProductType != default)
        {
            return policy.RequiresShipping(offer.ProductType);
        }

        return offer.FulfilmentType.RequiresShipping();
    }
}
