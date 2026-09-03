using ThreeCommerce.BuildingBlocks.Contracts.Catalog;

namespace ThreeCommerce.Ordering.Domain;

/// <summary>
/// One cart line as the promotion engine sees it: the OFFER-RESOLVED effective selling price
/// (excl. tax/shipping/fees, ADR-0047/0048) and its quantity. Index-stable — the outcome's per-line
/// allocation is parallel to the input list.
/// </summary>
public readonly record struct PromotionLine(Guid ProductId, long UnitPriceMinor, int Quantity)
{
    /// <summary>The line's item value: unit price × quantity, in minor units.</summary>
    public long TotalMinor => checked(UnitPriceMinor * Quantity);
}

/// <summary>
/// One promotion that qualified, reduced to what the selection step needs: how much it takes off, whether
/// it grants free shipping, whether it stacks, and which input line indexes its discount falls on.
/// Callers that own their own eligibility vocabulary (coupon codes, categories — <see cref="PricingEngine"/>)
/// build these directly; promotions projected from Catalog go through
/// <see cref="PromotionEvaluator.CandidateFor"/>.
/// </summary>
public sealed record PromotionCandidate(
    Guid PromotionId,
    long DiscountMinor,
    bool FreeShippingApplied,
    bool Combinable,
    IReadOnlyList<int> LineIndexes);

/// <summary>
/// What the engine decided. <see cref="LineDiscountsMinor"/> is parallel to the input lines and sums
/// EXACTLY to <see cref="DiscountMinor"/> (largest-remainder allocation), so callers can compute a tax
/// base per line — which is what keeps Net + Ship + Tax = Gross.
/// </summary>
public sealed record PromotionOutcome(
    long DiscountMinor,
    bool FreeShippingApplied,
    IReadOnlyList<Guid> AppliedPromotionIds,
    IReadOnlyList<long> LineDiscountsMinor)
{
    /// <summary>No promotion applied: zero discount, no free shipping, an all-zero line allocation.</summary>
    public static PromotionOutcome None(int lineCount) => new(0, false, [], new long[lineCount]);
}

/// <summary>
/// The one place threshold promotions are decided (ADR-0051). Pure: no EF, no HTTP, no ambient clock —
/// time is a parameter. Both <see cref="PricingEngine"/> and Ordering's checkout/cart-summary endpoints
/// call it, so the promotion algorithm exists exactly once and the engine can never diverge from what is
/// actually charged.
/// <para>
/// Order of operations: eligibility (tenant + storefront + currency + active + window) → threshold
/// measurement on the promotion's own scope base → reward → combinability selection → per-line
/// allocation. All arithmetic is integer minor units.
/// </para>
/// </summary>
public static class PromotionEvaluator
{
    /// <summary>
    /// Evaluates the tenant's projected promotions against a cart. <paramref name="shippingMinor"/> must be
    /// the shipping the cart would OTHERWISE pay — pass 0 when the cart already ships free, so a
    /// free-shipping promotion scores no phantom benefit.
    /// </summary>
    public static PromotionOutcome Evaluate(
        IReadOnlyList<PromotionLine> lines,
        IReadOnlyList<PromotionCopy> promotions,
        Guid tenantId,
        Guid storefrontId,
        string currency,
        long shippingMinor,
        DateTimeOffset now)
    {
        if (lines.Count == 0 || promotions.Count == 0)
        {
            return PromotionOutcome.None(lines.Count);
        }

        var candidates = new List<PromotionCandidate>();
        foreach (var promotion in promotions)
        {
            if (!promotion.IsEffectiveFor(tenantId, storefrontId, currency, now))
            {
                continue;
            }

            var candidate = CandidateFor(
                lines, promotion.PromotionId, promotion.Scope, promotion.ProductId,
                promotion.MinimumAmountMinor, promotion.MinimumQuantity,
                promotion.GrantsFreeShipping, promotion.PercentOff, promotion.DiscountAmountMinor,
                promotion.Combinable);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        return Select(lines, candidates, shippingMinor);
    }

    /// <summary>
    /// Measures one threshold rule against the cart and, if it qualifies, turns it into a candidate.
    /// Returns null when the rule is ineligible. The scope base is the whole cart for
    /// <see cref="PromotionScopeKind.Storefront"/> and only the named product's lines for
    /// <see cref="PromotionScopeKind.Product"/>; both thresholds set are ANDed (ADR-0051).
    /// </summary>
    public static PromotionCandidate? CandidateFor(
        IReadOnlyList<PromotionLine> lines,
        Guid promotionId,
        PromotionScopeKind scope,
        Guid? productId,
        long minimumAmountMinor,
        int minimumQuantity,
        bool grantsFreeShipping,
        int percentOff,
        long discountAmountMinor,
        bool combinable)
    {
        // The scope's contributing line indexes: the whole cart, or just the named product's lines
        // (a product may appear on several variant lines — they sum).
        var indexes = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (scope == PromotionScopeKind.Storefront || (productId is { } pid && lines[i].ProductId == pid))
            {
                indexes.Add(i);
            }
        }

        if (indexes.Count == 0)
        {
            return null;
        }

        long baseAmount = 0;
        var baseQuantity = 0;
        foreach (var i in indexes)
        {
            baseAmount = checked(baseAmount + lines[i].TotalMinor);
            baseQuantity = checked(baseQuantity + lines[i].Quantity);
        }

        // Both thresholds set ⇒ AND (ADR-0051 decision 3); an unset (0) threshold never blocks. Comparison
        // is >=, so a cart landing EXACTLY on the threshold qualifies.
        var met = (minimumAmountMinor == 0 || baseAmount >= minimumAmountMinor)
            && (minimumQuantity == 0 || baseQuantity >= minimumQuantity);
        if (!met)
        {
            return null;
        }

        // The reward rides on the SAME base the threshold was measured against. A fixed amount can never
        // exceed its own scope base (a $20-off promotion on a $5 product takes $5).
        var raw = percentOff > 0 ? checked(baseAmount * percentOff / 100) : discountAmountMinor;
        var discount = Math.Clamp(raw, 0, baseAmount);
        if (discount == 0 && !grantsFreeShipping)
        {
            return null;
        }

        return new PromotionCandidate(promotionId, discount, grantsFreeShipping, combinable, indexes);
    }

    /// <summary>
    /// Picks the winning promotion set and allocates its discount per line: the better of
    /// [best single Exclusive] vs [Σ all Combinable] by customer benefit
    /// (<c>discount + (freeShipping ? shippingMinor : 0)</c>). Ties go to the combinable set (it shows the
    /// shopper more applied promotions for the same money); within a branch, ties break on ascending
    /// promotion id. The combined discount is capped at the subtotal so the goods can never go negative,
    /// and the returned per-line vector sums to the reported discount EXACTLY.
    /// </summary>
    public static PromotionOutcome Select(
        IReadOnlyList<PromotionLine> lines,
        IReadOnlyList<PromotionCandidate> candidates,
        long shippingMinor)
    {
        if (lines.Count == 0 || candidates.Count == 0)
        {
            return PromotionOutcome.None(lines.Count);
        }

        long subtotal = 0;
        var lineTotals = new long[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            lineTotals[i] = lines[i].TotalMinor;
            subtotal = checked(subtotal + lineTotals[i]);
        }

        if (subtotal <= 0)
        {
            return PromotionOutcome.None(lines.Count);
        }

        // An Exclusive promotion never combines with anything — including other exclusives — so the
        // exclusive branch is a single best candidate.
        var bestExclusive = candidates
            .Where(c => !c.Combinable)
            .OrderByDescending(c => Benefit(c.DiscountMinor, c.FreeShippingApplied, shippingMinor))
            .ThenBy(c => c.PromotionId)
            .FirstOrDefault();
        var exclusiveSet = bestExclusive is null ? Array.Empty<PromotionCandidate>() : [bestExclusive];
        var exclusiveDiscount = Math.Min(bestExclusive?.DiscountMinor ?? 0, subtotal);
        var exclusiveFreeShipping = bestExclusive?.FreeShippingApplied == true;

        var combinableSet = candidates.Where(c => c.Combinable).OrderBy(c => c.PromotionId).ToArray();
        long combinableRaw = 0;
        var combinableFreeShipping = false;
        foreach (var c in combinableSet)
        {
            combinableRaw = checked(combinableRaw + c.DiscountMinor);
            combinableFreeShipping |= c.FreeShippingApplied;
        }

        var combinableDiscount = Math.Min(combinableRaw, subtotal);

        var winners = combinableSet.Length > 0
            && Benefit(combinableDiscount, combinableFreeShipping, shippingMinor)
                >= Benefit(exclusiveDiscount, exclusiveFreeShipping, shippingMinor)
            ? combinableSet
            : exclusiveSet;
        if (winners.Length == 0)
        {
            return PromotionOutcome.None(lines.Count);
        }

        var freeShipping = winners.Any(w => w.FreeShippingApplied);
        var rawDiscount = winners.Sum(w => w.DiscountMinor);
        var discountMinor = Math.Min(rawDiscount, subtotal);

        // Per-promotion largest-remainder allocation over that promotion's own contributing lines, summed.
        var allocation = new long[lines.Count];
        foreach (var winner in winners)
        {
            var parts = AllocateByWeight(winner.DiscountMinor, winner.LineIndexes, lineTotals);
            for (var k = 0; k < winner.LineIndexes.Count; k++)
            {
                allocation[winner.LineIndexes[k]] += parts[k];
            }
        }

        // The subtotal cap can shave the total below the sum of the parts — scale the vector back down by
        // the same largest-remainder rule so it still sums EXACTLY to the reported discount.
        if (rawDiscount > discountMinor)
        {
            var allIndexes = Enumerable.Range(0, lines.Count).ToArray();
            var scaled = AllocateByWeight(discountMinor, allIndexes, allocation);
            allocation = scaled;
        }

        ClampToLineTotals(allocation, lineTotals);
        return new PromotionOutcome(
            discountMinor, freeShipping, winners.Select(w => w.PromotionId).ToArray(), allocation);
    }

    private static long Benefit(long discountMinor, bool freeShipping, long shippingMinor) =>
        checked(discountMinor + (freeShipping ? shippingMinor : 0));

    /// <summary>
    /// Splits <paramref name="amount"/> across <paramref name="indexes"/> in proportion to
    /// <paramref name="weights"/> using LARGEST REMAINDER, so the parts sum to exactly the amount. A naive
    /// per-line <c>amount × share / total</c> loses pennies and breaks Net + Ship + Tax = Gross. Int128 is
    /// used for the intermediate product so a large cart cannot overflow (no floating point anywhere).
    /// </summary>
    private static long[] AllocateByWeight(long amount, IReadOnlyList<int> indexes, IReadOnlyList<long> weights)
    {
        var parts = new long[indexes.Count];
        if (amount <= 0 || indexes.Count == 0)
        {
            return parts;
        }

        Int128 total = 0;
        foreach (var i in indexes)
        {
            total += weights[i];
        }

        if (total <= 0)
        {
            // No weight to spread across (e.g. an all-zero-priced scope): put it on the first line so the
            // vector still sums to the amount.
            parts[0] = amount;
            return parts;
        }

        var remainders = new (int Slot, Int128 Remainder)[indexes.Count];
        long allocated = 0;
        for (var k = 0; k < indexes.Count; k++)
        {
            var product = (Int128)amount * weights[indexes[k]];
            var share = (long)(product / total);
            parts[k] = share;
            allocated += share;
            remainders[k] = (k, product % total);
        }

        // Hand out the rounding leftovers to the largest fractional remainders first; ties go to the
        // earliest line so the split is deterministic.
        var leftover = amount - allocated;
        foreach (var (slot, _) in remainders.OrderByDescending(r => r.Remainder).ThenBy(r => r.Slot))
        {
            if (leftover <= 0)
            {
                break;
            }

            parts[slot]++;
            leftover--;
        }

        return parts;
    }

    /// <summary>
    /// No line may be discounted below zero. Stacked promotions can over-allocate a single line (a
    /// product-scoped promotion plus a cart-wide one), so cap each line at its own total and push the
    /// excess onto the lines that still have headroom — the vector's SUM is preserved, which is what the
    /// tax base and the money identity depend on.
    /// </summary>
    private static void ClampToLineTotals(long[] allocation, long[] lineTotals)
    {
        long excess = 0;
        for (var i = 0; i < allocation.Length; i++)
        {
            if (allocation[i] > lineTotals[i])
            {
                excess += allocation[i] - lineTotals[i];
                allocation[i] = lineTotals[i];
            }
        }

        if (excess == 0)
        {
            return;
        }

        // Fill the roomiest lines first; the caller has already capped the total at the subtotal, so the
        // available headroom always absorbs the excess.
        foreach (var i in Enumerable.Range(0, allocation.Length)
            .OrderByDescending(i => lineTotals[i] - allocation[i])
            .ThenBy(i => i))
        {
            if (excess <= 0)
            {
                break;
            }

            var take = Math.Min(excess, lineTotals[i] - allocation[i]);
            allocation[i] += take;
            excess -= take;
        }
    }
}
