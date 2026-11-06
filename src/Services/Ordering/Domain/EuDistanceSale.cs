namespace ThreeCommerce.Ordering.Domain;

/// <summary>
/// One confirmed intra-EU distance sale's contribution to its seller's EUR 10,000 threshold (ADR-0063 P1b): an
/// order of a storefront taxed under EU rules (its <see cref="Order.TaxJurisdiction"/> is a Member State) whose
/// destination is ANOTHER Member State. Written once per order, in the same transaction as the order itself, so
/// the running total per tenant (the seller, ADR-0063 D1) per calendar year is exactly Σ of these rows — never a
/// shared counter row, which the confirming consumer's REPEATABLE READ transaction would turn into 40001s under
/// concurrent confirmations (see <c>OrderNumberAllocator</c>). Append-only; the key is (order, destination) — one row
/// per Member State the sale crossed into (ADR-0063 § 16: a sale whose lines go to different countries records each
/// destination's share) — so a redelivered confirmation can never count a sale twice.
/// </summary>
public class EuDistanceSale
{
    public Guid OrderId { get; init; }
    public Guid TenantId { get; init; }

    /// <summary>The UTC calendar year of the supply (the checkout), the threshold's period.</summary>
    public int Year { get; init; }

    public required string HomeCountry { get; init; }
    public required string DestinationCountry { get; init; }

    /// <summary>The order currency. Only EUR rows count toward the EUR threshold until an FX source exists (D13).</summary>
    public required string Currency { get; init; }

    /// <summary>The sale's value net of VAT (<see cref="EuOssTaxStrategy.DistanceSaleValue"/>), in <see cref="Currency"/> minor units.</summary>
    public long NetMinor { get; init; }

    public TaxScheme TaxScheme { get; init; }
    public DateTimeOffset RecordedAt { get; init; }

    /// <summary>
    /// The contributions of <paramref name="order"/>: one row per Member State OTHER than home that some component
    /// of the sale was supplied into (ADR-0063 § 16, per-line destination), carrying Σ of those components' net
    /// bases — each line at its own <see cref="OrderLine.TaxDestinationCountry"/>, the shipping line at the order's
    /// destination (where the goods go). A sale with one destination is one row with the whole sale, exactly as
    /// before; a sale split across destinations counts only its cross-border parts, each under its own country.
    /// Empty when nothing crossed a border (a domestic, non-EU or pre-P1b order — whose
    /// <see cref="Order.TaxJurisdiction"/> is empty — counts nothing).
    /// </summary>
    public static IReadOnlyList<EuDistanceSale> For(Order order, DateTimeOffset suppliedAt, DateTimeOffset now)
    {
        var home = EuMemberStates.OfJurisdiction(order.TaxJurisdiction);
        if (home is null)
        {
            return [];
        }

        string? CrossBorder(string destination) =>
            EuMemberStates.Normalize(destination) is { } memberState && memberState != home ? memberState : null;

        var net = new Dictionary<string, long>(StringComparer.Ordinal);
        if (CrossBorder(order.TaxDestinationCountry) is { } shippingDestination)
        {
            net[shippingDestination] = order.ShippingTaxableBaseMinor;
        }

        foreach (var line in order.Lines)
        {
            if (CrossBorder(line.DestinationOr(order.TaxDestinationCountry)) is { } destination)
            {
                net[destination] = net.GetValueOrDefault(destination) + line.TaxableBaseMinor;
            }
        }

        return net.OrderBy(n => n.Key, StringComparer.Ordinal).Select(n => new EuDistanceSale
        {
            OrderId = order.Id,
            TenantId = order.TenantId,
            Year = suppliedAt.UtcDateTime.Year,
            HomeCountry = home,
            DestinationCountry = n.Key,
            Currency = order.Currency.ToUpperInvariant(),
            NetMinor = n.Value,
            TaxScheme = order.TaxScheme,
            RecordedAt = now,
        }).ToList();
    }
}
