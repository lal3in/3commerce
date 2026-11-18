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

    /// <summary>
    /// Which supply of the order the row counts (ADR-0063 § 16, OSS threshold crossing): <c>""</c> for the checkout sale
    /// itself; the renewal's ledger reference (<c>renew-{subscriptionId}-{sequence}</c>) for a settled renewal of a
    /// subscription line — each period is a new supply (§ 7.1), so it counts toward the threshold too. Part of the key,
    /// so a redelivered renewal counts once.
    /// </summary>
    public string SourceReference { get; init; } = string.Empty;

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
    /// <see cref="Order.TaxJurisdiction"/> is empty — counts nothing). Goods into Northern Ireland count under
    /// <c>XI</c>; a component exported to a territory outside the EU VAT area
    /// (<see cref="TaxExemptionReason.OutsideEuVatArea"/>) is not a distance sale and never counts.
    /// </summary>
    public static IReadOnlyList<EuDistanceSale> For(Order order, DateTimeOffset suppliedAt, DateTimeOffset now)
    {
        var home = EuMemberStates.OfJurisdiction(order.TaxJurisdiction);
        if (home is null)
        {
            return [];
        }

        string? CrossBorder(string destination, TaxExemptionReason exemption) =>
            exemption != TaxExemptionReason.OutsideEuVatArea && EuVatArea.Normalize(destination) is { } area && area != home ? area : null;

        var net = new Dictionary<string, long>(StringComparer.Ordinal);
        if (CrossBorder(order.TaxDestinationCountry, order.ShippingExemptionReason) is { } shippingDestination)
        {
            net[shippingDestination] = order.ShippingTaxableBaseMinor;
        }

        foreach (var line in order.Lines)
        {
            if (CrossBorder(line.DestinationOr(order.TaxDestinationCountry), line.ExemptionReason) is { } destination)
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

    /// <summary>
    /// A settled renewal's contribution (ADR-0063 § 16, OSS threshold crossing): a renewal of a subscription line is a new
    /// supply each period (§ 7.1), so a renewal of a line supplied into ANOTHER Member State counts toward the seller's
    /// threshold exactly as the checkout sale did. The destination is the LINE's place of supply at purchase (the bill-to
    /// for an electronically supplied service), the home is the order's recorded jurisdiction, and the value is the
    /// renewal's net of VAT as charged (<paramref name="netMinor"/>). Null when the renewal is not a distance sale: a
    /// non-EU or pre-P1b order, a home-country or non-EU destination, an export from the EU VAT area, or nothing net.
    /// </summary>
    public static EuDistanceSale? ForRenewal(
        Order order, OrderLine line, string reference, string currency, long netMinor, TaxScheme scheme,
        DateTimeOffset settledAt, DateTimeOffset now)
    {
        var home = EuMemberStates.OfJurisdiction(order.TaxJurisdiction);
        if (home is null || netMinor <= 0 || scheme == TaxScheme.Export || line.ExemptionReason == TaxExemptionReason.OutsideEuVatArea)
        {
            return null;
        }

        if (EuVatArea.Normalize(line.DestinationOr(order.TaxDestinationCountry)) is not { } destination || destination == home)
        {
            return null;
        }

        return new EuDistanceSale
        {
            OrderId = order.Id,
            TenantId = order.TenantId,
            Year = settledAt.UtcDateTime.Year,
            HomeCountry = home,
            DestinationCountry = destination,
            SourceReference = reference,
            Currency = currency.ToUpperInvariant(),
            NetMinor = netMinor,
            TaxScheme = scheme,
            RecordedAt = StoredTime.Of(now),
        };
    }
}
