namespace ThreeCommerce.Payments.Domain;

/// <summary>
/// One immutable row per billing period a subscription has been through (mt7_3 history). Sequence 1 is
/// the first period opened at signup; each successful <see cref="Subscription.Renew"/> appends the next.
/// Written in the same SaveChanges as the state change (via the Subscription aggregate) so it can't drift.
/// </summary>
public sealed class SubscriptionRenewal
{
    public Guid Id { get; init; }
    public Guid SubscriptionId { get; init; }
    public int Sequence { get; init; }
    public DateTimeOffset PeriodStart { get; init; }
    public DateTimeOffset PeriodEnd { get; init; }
    /// <summary>
    /// The gross charged for a renewed period (Sequence ≥ 2): the price, plus the added tax on an exclusive (US) store
    /// (owner decision 2026-10-15). Sequence 1 records the subscription's price (the first period was paid with the order).
    /// </summary>
    public long AmountMinor { get; init; }
    public required string Currency { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
}
