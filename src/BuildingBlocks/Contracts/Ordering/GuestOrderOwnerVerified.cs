namespace ThreeCommerce.BuildingBlocks.Contracts.Ordering;

/// <summary>
/// Internal to Ordering (FR-7): the email a GUEST order was placed with has been verified by
/// <paramref name="UserId"/>, so the order belongs to that account. Ordering's EmailVerified sweep publishes
/// one per matching guest order instead of updating the rows itself; the order-status consumer applies it on
/// the order's partition, in line with the order's other events, so nothing writes one order row concurrently.
/// Only fills a missing owner — an order that already has one keeps it.
/// </summary>
public record GuestOrderOwnerVerified(Guid OrderId, Guid UserId);
