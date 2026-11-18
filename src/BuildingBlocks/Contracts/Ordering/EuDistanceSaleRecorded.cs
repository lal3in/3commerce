namespace ThreeCommerce.BuildingBlocks.Contracts.Ordering;

/// <summary>
/// One row of a seller's EU distance-sales running total was recorded (ADR-0063 P1b and § 16, OSS threshold crossing):
/// a confirmed intra-EU distance sale, or a settled renewal of one, counts toward the seller's EUR 10,000 EU-wide
/// threshold (Art. 59c). Ordering owns the running total (<c>ordering."EuDistanceSales"</c>) and publishes one of these
/// per row, through the bus outbox, in the SAME transaction that writes the row, so a consumer's copy sums to exactly
/// Ordering's total. Payments keeps that copy to re-decide a renewal's scheme against the seller's turnover at each
/// charge. The key is (<see cref="OrderId"/>, <see cref="DestinationCountry"/>, <see cref="SourceReference"/>): a
/// consumer that stores it idempotently never counts a row twice.
/// </summary>
/// <param name="TenantId">The seller (the tenant is the seller of record, ADR-0063 D1).</param>
/// <param name="OrderId">The order the sale belongs to (a renewal's is its subscription's order).</param>
/// <param name="SourceReference">
/// <c>""</c> for the checkout sale itself; the renewal's ledger reference (<c>renew-{subscriptionId}-{sequence}</c>) for a
/// settled renewal.
/// </param>
/// <param name="HomeCountry">The seller's home Member State (the storefront's tax jurisdiction as applied).</param>
/// <param name="DestinationCountry">The Member State the sale was supplied into (<c>XI</c> for goods into Northern Ireland).</param>
/// <param name="Year">The UTC calendar year of the supply — the threshold's period.</param>
/// <param name="Currency">The sale's currency. Only EUR rows count toward the EUR threshold until an FX source exists (D13).</param>
/// <param name="NetMinor">The sale's value net of VAT, in <see cref="Currency"/> minor units.</param>
/// <param name="RecordedAt">When Ordering recorded the row.</param>
public record EuDistanceSaleRecorded(
    Guid TenantId,
    Guid OrderId,
    string SourceReference,
    string HomeCountry,
    string DestinationCountry,
    int Year,
    string Currency,
    long NetMinor,
    DateTimeOffset RecordedAt);
