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
    public long AmountMinor { get; init; }
    public required string Currency { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
}
