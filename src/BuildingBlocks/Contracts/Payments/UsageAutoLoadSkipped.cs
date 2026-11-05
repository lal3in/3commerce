using ThreeCommerce.BuildingBlocks.Contracts.Supply;

namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

/// <summary>Why Payments did not charge an auto-load top-up at all. Crosses as a number (append only).</summary>
public enum UsageAutoLoadSkipReason
{
    /// <summary>The customer has no default saved card to charge off-session.</summary>
    NoSavedCard = 1,
}

/// <summary>
/// Payments did NOT charge a prepaid auto-load top-up (<see cref="UsageAutoLoadCharge"/>) because there was nothing to
/// charge it to (owner decision 2026-11-06, <c>jobmgr_3_settlement</c>): an auto top-up charges the customer's DEFAULT
/// saved card off-session, and with no saved card the top-up is skipped, not attempted. Nothing was charged, nothing
/// posted, and the charge is not dunned (nothing is owed). Published through the bus outbox once per reference.
/// <para>
/// Usage takes back the top-up's optimistic credit and pauses auto-load for the balance until the customer has a card
/// (<see cref="CustomerPaymentMethodReady"/>). Notifications emails the customer to add one.
/// </para>
/// </summary>
/// <param name="Quantity">The reload units the top-up would have credited.</param>
/// <param name="AmountMinor">The top-up's shelf price (what Usage asked for; no tax was resolved).</param>
public record UsageAutoLoadSkipped(
    Guid TenantId,
    Guid? StorefrontId,
    string CustomerEmail,
    MeterType Meter,
    long Quantity,
    string Reference,
    long AmountMinor,
    string Currency,
    UsageAutoLoadSkipReason Reason,
    DateTimeOffset SkippedAt);
