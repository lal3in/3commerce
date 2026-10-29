using ThreeCommerce.BuildingBlocks.Contracts.Supply;

namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

/// <summary>Why a charge the provider answered did not settle. Crosses as a number.</summary>
public enum ChargeFailureReason
{
    /// <summary>Declined.</summary>
    Failed = 1,

    /// <summary>The bank wants the customer to authenticate (3-D Secure / SCA) before it pays.</summary>
    RequiresAction = 2,

    /// <summary>Cancelled at the provider.</summary>
    Cancelled = 3,
}

/// <summary>
/// An attempt of a metered usage charge was ANSWERED by the provider without settling (owner decision 2026-10-27,
/// <c>usage_charge_failure_feedback</c>): nothing was charged and nothing posted. Published through the bus outbox with the
/// charge's dunning state, once per answered attempt. The same retry rules as a subscription renewal apply (ADR-0057):
/// Payments retries on days 1, 3 and 7 after the first failure, each with a fresh provider key and the SAME pinned amount.
/// <para>
/// Usage reverses the optimistic prepaid credit / marks the overage unpaid on the first one, and suspends further
/// billable usage of the balance when <see cref="Final"/> (the day-7 retry did not settle and the charge was abandoned).
/// Notifications emails the customer. A later <see cref="UsageChargeSettled"/> for the same <see cref="Reference"/>
/// supersedes it.
/// </para>
/// </summary>
/// <param name="AmountMinor">The pinned charge (price + any added tax on an exclusive store).</param>
/// <param name="Attempt">Which attempt failed (0 = the first).</param>
/// <param name="NextRetryAt">When it is retried if nothing else happens (null when no retry is left).</param>
/// <param name="AbandonsAfter">When an unpaid charge is abandoned: the final scheduled retry.</param>
/// <param name="Final">The final retry did not settle: the charge is abandoned and is not retried again.</param>
public record UsageChargeFailed(
    Guid TenantId,
    Guid? StorefrontId,
    string CustomerEmail,
    UsageChargeKind Kind,
    MeterType Meter,
    long Quantity,
    string Reference,
    long AmountMinor,
    string Currency,
    ChargeFailureReason Reason,
    int Attempt,
    DateTimeOffset FailedAt,
    DateTimeOffset? NextRetryAt,
    DateTimeOffset? AbandonsAfter,
    bool Final);
