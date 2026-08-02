namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

/// <summary>
/// Published when a chargeback (dispute) is opened against an order's captured payment (phase 2). The
/// ledger has already reversed the sale and booked the dispute fee; Ordering consumes this to flag the
/// order as disputed so operators and the shopper see its state. <see cref="AmountMinor"/> is the
/// disputed (reversed) gross.
/// </summary>
public record PaymentDisputed(Guid OrderId, string PaymentIntentId, long AmountMinor);
