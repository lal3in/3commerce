namespace ThreeCommerce.Support.Domain;

/// <summary>One order line's share of an RMA refund: the returned units and the money given back for them.</summary>
public sealed record RmaRefundLineShare(Guid OrderLineId, int Quantity, long AmountMinor);

/// <summary>
/// Which order lines an RMA refund's money is for (ADR-0063 § 6.2, P5) — the attribution a credit note credits line by
/// line. Pure: built from the RMA's recorded lines and the amount actually requested.
/// <para>
/// Each line is worth its DISCOUNTED value (<see cref="RmaRequestLine.RefundableMinor"/>, ADR-0055). When the order's
/// refundable-gross cap made the request smaller than those values add up to, the amount is spread over the lines in
/// proportion to their value, largest remainder, ties to the earliest line — so the shares always sum to the amount
/// requested exactly, which is what the ledger reverses. Shipping is never part of an RMA (the lines are goods).
/// </para>
/// </summary>
public static class RmaRefundAttribution
{
    /// <summary>
    /// The per-line shares of <paramref name="amountMinor"/>, or null when the refund cannot be attributed and must go
    /// out amount-only: no recorded lines, a line from a snapshot that predates <c>OrderLineId</c>, or lines worth less
    /// than the amount (never produced by the RMA endpoints, which cap the amount at the lines' value).
    /// </summary>
    public static IReadOnlyList<RmaRefundLineShare>? For(IReadOnlyList<RmaRequestLine> lines, long amountMinor)
    {
        if (lines.Count == 0 || amountMinor <= 0 || lines.Any(l => l.OrderLineId == Guid.Empty))
        {
            return null;
        }

        var values = lines.Select(l => l.RefundableMinor()).ToArray();
        var total = values.Sum();
        if (total < amountMinor)
        {
            return null;
        }

        var shares = new long[values.Length];
        if (total == amountMinor)
        {
            values.CopyTo(shares, 0);
        }
        else
        {
            // floor(value × amount / total) per line, then the leftover minor units one each to the largest remainders.
            var remainders = new decimal[values.Length];
            long allocated = 0;
            for (var i = 0; i < values.Length; i++)
            {
                var exact = (decimal)values[i] * amountMinor / total;
                shares[i] = (long)decimal.Floor(exact);
                remainders[i] = exact - shares[i];
                allocated += shares[i];
            }

            var order = Enumerable.Range(0, values.Length).OrderByDescending(i => remainders[i]).ThenBy(i => i).ToArray();
            for (var k = 0; allocated < amountMinor; k++, allocated++)
            {
                shares[order[k % order.Length]]++;
            }
        }

        return lines.Select((l, i) => new RmaRefundLineShare(l.OrderLineId, l.Quantity, shares[i])).ToList();
    }
}
