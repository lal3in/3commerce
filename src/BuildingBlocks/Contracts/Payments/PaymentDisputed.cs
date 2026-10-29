namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

/// <summary>
/// Published when a chargeback (dispute) is opened against an order's captured payment (phase 2). The
/// ledger has already reversed the sale and booked the dispute fee; Ordering consumes this to flag the
/// order as disputed so operators and the shopper see its state. <see cref="AmountMinor"/> is the
/// disputed (reversed) gross.
/// <see cref="ChargeReference"/> (appended, renewal_usage_refund_path) is null for the order's checkout payment and
/// names the disputed renewal / usage charge otherwise — not the order's dispute, so Ordering leaves the order alone.
/// </summary>
public record PaymentDisputed(Guid OrderId, string PaymentIntentId, long AmountMinor, string? ChargeReference = null);
