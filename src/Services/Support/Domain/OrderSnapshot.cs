namespace ThreeCommerce.Support.Domain;

/// <summary>
/// Local read copy of a confirmed order (lines + prices), fed by OrderConfirmed events
/// (ADR-0008). Lets Support compute refund amounts server-side from the order itself,
/// so the customer selects lines rather than sending a trusted amount (BL-8).
/// </summary>
public class OrderSnapshot
{
    public Guid OrderId { get; init; }
    public required string Email { get; init; }

    /// <summary>
    /// What the shopper was actually charged for this order (items − discount + shipping + tax). It is
    /// the CEILING on everything an RMA can ever give back: the sum of a discounted order's listed line
    /// values exceeds it, so refunds are capped here (rma_disc).
    /// </summary>
    public long GrossMinor { get; init; }
    public required string Currency { get; init; }
    public List<OrderSnapshotLine> Lines { get; init; } = [];
}

public class OrderSnapshotLine
{
    public Guid Id { get; init; }
    public Guid OrderId { get; init; }
    public Guid ProductId { get; init; }
    public required string Title { get; init; }

    /// <summary>The LISTED per-unit price. On its own it is NOT a refund basis — see
    /// <see cref="DiscountMinor"/>.</summary>
    public long UnitPriceMinor { get; init; }

    /// <summary>
    /// This line's whole allocated share of the order's discount (promotion + storefront-wide), carried
    /// on <c>OrderLineInfo</c>. The line's real selling value — and so the money a return gives back —
    /// is <c>UnitPriceMinor × Quantity − DiscountMinor</c> (rma_disc).
    /// <para>
    /// Snapshots projected before this field existed hold 0, so their refunds fall back to the list
    /// price exactly as before; <see cref="OrderSnapshot.GrossMinor"/> is what keeps that safe.
    /// </para>
    /// </summary>
    public long DiscountMinor { get; init; }

    public int Quantity { get; init; }

    /// <summary>The refundable value of <paramref name="quantity"/> units of this line: their share of
    /// the listed value less the SAME share of the line's discount, never below zero.</summary>
    public long RefundableMinor(int quantity)
    {
        var qty = Math.Clamp(quantity, 0, Quantity);
        if (qty == 0)
        {
            return 0;
        }

        var listed = UnitPriceMinor * qty;
        // Pro-rate the line's discount over the returned units (whole line => the whole discount, so a
        // full return of a line always gives back exactly what it was sold for).
        var discount = qty == Quantity
            ? DiscountMinor
            : (long)Math.Round((decimal)DiscountMinor * qty / Quantity, MidpointRounding.AwayFromZero);
        return Math.Max(0, listed - discount);
    }
}
