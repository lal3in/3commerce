namespace ThreeCommerce.Ordering.Domain;

/// <summary>
/// One confirmed intra-EU distance sale's contribution to its seller's EUR 10,000 threshold (ADR-0063 P1b): an
/// order of a storefront taxed under EU rules (its <see cref="Order.TaxJurisdiction"/> is a Member State) whose
/// destination is ANOTHER Member State. Written once per order, in the same transaction as the order itself, so
/// the running total per tenant (the seller, ADR-0063 D1) per calendar year is exactly Σ of these rows — never a
/// shared counter row, which the confirming consumer's REPEATABLE READ transaction would turn into 40001s under
/// concurrent confirmations (see <c>OrderNumberAllocator</c>). Append-only; the key is the order, so a redelivered
/// confirmation can never count a sale twice.
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
    /// The contribution of <paramref name="order"/>, or null when it is not an intra-EU distance sale (a domestic,
    /// non-EU or pre-P1b order — whose <see cref="Order.TaxJurisdiction"/> is empty — counts nothing).
    /// </summary>
    public static EuDistanceSale? For(Order order, DateTimeOffset suppliedAt, DateTimeOffset now)
    {
        var home = EuMemberStates.OfJurisdiction(order.TaxJurisdiction);
        var destination = EuMemberStates.Normalize(order.TaxDestinationCountry);
        if (home is null || destination is null || home == destination)
        {
            return null;
        }

        return new EuDistanceSale
        {
            OrderId = order.Id,
            TenantId = order.TenantId,
            Year = suppliedAt.UtcDateTime.Year,
            HomeCountry = home,
            DestinationCountry = destination,
            Currency = order.Currency.ToUpperInvariant(),
            NetMinor = order.Lines.Sum(l => l.TaxableBaseMinor) + order.ShippingTaxableBaseMinor,
            TaxScheme = order.TaxScheme,
            RecordedAt = now,
        };
    }
}
