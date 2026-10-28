using ThreeCommerce.BuildingBlocks.Contracts.Tax.Facts;

namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

/// <summary>
/// A subscription renewal SETTLED: the provider took the money, the period advanced and the ledger posted its sale
/// (ADR-0063 § 6.2 / P4b). Published through the bus outbox in the SAME unit of work as the
/// <c>renew-{SubscriptionId}-{Sequence}</c> journal entry, exactly once per renewed period (a renewal settled off-session,
/// or later by the provider's success event after 3-D Secure on the "complete payment" page). Never published for an
/// attempt that did not settle (Failed / RequiresAction / Cancelled — those dun, ADR-0057). The Invoicing service issues
/// one invoice per event, keyed <c>Renewal:{SubscriptionId}:{Sequence}</c>.
/// </summary>
/// <param name="Sequence">The period's history sequence (2 = the first renewal; 1 was paid with the order).</param>
/// <param name="PeriodStart">The supply period the charge pays for.</param>
/// <param name="PeriodEnd">The supply period's end.</param>
/// <param name="ChargeReference">The ledger reference of the sale, <c>renew-{SubscriptionId}-{Sequence}</c>.</param>
/// <param name="NetMinor">Revenue: <see cref="GrossMinor"/> less <see cref="TaxMinor"/>.</param>
/// <param name="TaxMinor">The tax posted to <c>tax.store-{id}</c> (Σ of the lines' tax).</param>
/// <param name="GrossMinor">What the customer was charged.</param>
/// <param name="TaxInclusive">Whether the price contains the tax (inclusive regime) or the tax was added (exclusive).</param>
/// <param name="TaxScheme">The scheme (Domestic: Payments charges the storefront's own rate), NotRecorded when the rate was unknown.</param>
/// <param name="Lines">The charged lines with their tax facts (one: the subscription line).</param>
/// <param name="SettledAt">When the charge settled (the ledger entry's time).</param>
/// <param name="PaymentIntentId">The provider's intent that settled it.</param>
public record SubscriptionRenewalSettled(
    Guid TenantId,
    Guid? StorefrontId,
    Guid SubscriptionId,
    Guid OrderId,
    string CustomerEmail,
    Guid ProductId,
    Guid? VariantId,
    int Sequence,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    string ChargeReference,
    string Currency,
    long NetMinor,
    long TaxMinor,
    long GrossMinor,
    bool TaxInclusive,
    TaxScheme TaxScheme,
    IReadOnlyList<SettledChargeLine> Lines,
    DateTimeOffset SettledAt,
    string? PaymentIntentId = null);
