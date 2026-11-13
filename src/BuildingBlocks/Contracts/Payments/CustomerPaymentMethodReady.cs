namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

/// <summary>
/// The customer now has a default saved card Payments can charge off-session (a card saved as, or made, the default — or
/// Payments learnt which customer an existing default card belongs to). Usage resumes the auto-load top-ups it paused for
/// want of a card (<see cref="UsageAutoLoadSkipped"/>, owner decision 2026-11-06). Carries no card details.
/// </summary>
public record CustomerPaymentMethodReady(Guid TenantId, string CustomerEmail, DateTimeOffset ReadyAt);
