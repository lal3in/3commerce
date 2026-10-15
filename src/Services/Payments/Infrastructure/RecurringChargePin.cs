using ThreeCommerce.Payments.Domain.Ledger;
using ThreeCommerce.Payments.Infrastructure.Idempotency;

namespace ThreeCommerce.Payments.Infrastructure;

/// <summary>
/// Pins the amount of a renewal / usage charge to its provider idempotency key (owner decision 2026-10-15, ADR-0057
/// amendment). On an exclusive store the charge is price + tax at the rate in force, so a retry of the SAME key after
/// the store's rate changed would otherwise send a different amount — and a real provider rejects a reused key with
/// different parameters (Stripe <c>idempotency_error</c>), failing that charge permanently. The first attempt computes
/// the split once and PERSISTS it (an <see cref="Domain.IdempotencyRecord"/> under <c>charge-amount:{key}</c>, committed
/// BEFORE the provider call, so even a crash between the charge and our save replays the same amount); every retry of
/// that key — a re-run renewal of the same period, a redelivered usage message — reuses it verbatim, charges the same
/// amount under the same key (so the provider de-duplicates rather than double-charging) and posts the same split.
/// </summary>
public static class RecurringChargePin
{
    private const string KeyPrefix = "charge-amount:";

    /// <summary>The persisted shape (a plain record so it round-trips through JSON exactly).</summary>
    private sealed record PinnedCharge(long PriceMinor, long TaxMinor, RecurringChargeTaxBasis Basis);

    /// <summary>
    /// The split pinned to <paramref name="idempotencyKey"/>: the stored one when a previous attempt already pinned it,
    /// else <paramref name="compute"/>'s result, persisted (SaveChanges) before returning. Callers keep no other
    /// change pending on <paramref name="db"/> at this point (each renewal / usage charge commits its own outcome),
    /// so this commit writes the pin and nothing else.
    /// </summary>
    public static async Task<RecurringChargeTaxResult> PinAsync(
        PaymentsDbContext db, TimeProvider time, string idempotencyKey, Func<RecurringChargeTaxResult> compute, CancellationToken ct)
    {
        var pinned = await new IdempotencyGuard(db, time).ExecuteAsync(
            KeyPrefix + idempotencyKey,
            new { idempotencyKey },
            _ =>
            {
                var tax = compute();
                return Task.FromResult(new PinnedCharge(tax.PriceMinor, tax.TaxMinor, tax.Basis));
            },
            ct);
        return new RecurringChargeTaxResult(pinned.PriceMinor, pinned.TaxMinor, pinned.Basis);
    }
}
