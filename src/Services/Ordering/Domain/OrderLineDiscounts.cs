namespace ThreeCommerce.Ordering.Domain;

/// <summary>
/// Splits an order's TOTAL discount across its lines (rma_disc).
/// <para>
/// Checkout records two different things: <see cref="OrderLine.DiscountMinor"/> is the promotion
/// allocation for that line (product-scoped promotions land on the lines they cover), while the
/// storefront-wide percentage stays at the order level in <see cref="Order.DiscountMinor"/>. Neither
/// alone answers "what was this line actually sold for", which is exactly the question a refund asks —
/// so this puts the two together into one per-line number whose parts sum to
/// <see cref="Order.DiscountMinor"/> exactly (largest remainder, the same rule checkout used).
/// </para>
/// </summary>
public static class OrderLineDiscounts
{
    /// <summary>
    /// The whole allocated discount per line, index-aligned with <paramref name="order"/>'s
    /// <see cref="Order.Lines"/>.
    /// </summary>
    public static long[] For(Order order) => For(
        order.Lines.Select(l => l.UnitPriceMinor * l.Quantity).ToArray(),
        order.Lines.Select(l => l.DiscountMinor).ToArray(),
        order.DiscountMinor);

    /// <param name="lineTotalsMinor">Each line's listed value (unit price × quantity).</param>
    /// <param name="promotionDiscountsMinor">The promotion allocation already recorded per line.</param>
    /// <param name="totalDiscountMinor">The order's whole discount — promotion AND storefront-wide,
    /// after checkout's clamp at the subtotal.</param>
    public static long[] For(
        IReadOnlyList<long> lineTotalsMinor,
        IReadOnlyList<long> promotionDiscountsMinor,
        long totalDiscountMinor)
    {
        var count = lineTotalsMinor.Count;
        if (count == 0 || totalDiscountMinor <= 0)
        {
            return new long[count];
        }

        var promotionSum = 0L;
        for (var i = 0; i < count; i++)
        {
            promotionSum += Math.Max(0, promotionDiscountsMinor[i]);
        }

        if (promotionSum >= totalDiscountMinor)
        {
            // No storefront-wide remainder — or checkout's subtotal clamp cut the promotion back. Either
            // way the order's ACTUAL discount is spread over the shape the promotion chose (falling back
            // to line value when the promotion allocated nothing), never over the raw promotion vector,
            // which may sum to more than was really taken off.
            var weights = promotionSum > 0 ? promotionDiscountsMinor : lineTotalsMinor;
            return PromotionEvaluator.AllocateProportionally(totalDiscountMinor, weights);
        }

        // The storefront-wide percentage is uniform, so it spreads by line value; the promotion part
        // keeps the placement checkout gave it.
        var remainder = PromotionEvaluator.AllocateProportionally(totalDiscountMinor - promotionSum, lineTotalsMinor);
        var allocated = new long[count];
        for (var i = 0; i < count; i++)
        {
            allocated[i] = Math.Max(0, promotionDiscountsMinor[i]) + remainder[i];
        }

        return allocated;
    }
}
