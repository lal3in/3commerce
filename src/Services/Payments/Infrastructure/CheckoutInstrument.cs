using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Infrastructure;

/// <summary>
/// What a checkout's payment intent is created against (<c>AuthorizePaymentConsumer</c>): the signed-in member's
/// provider customer (their vault — a card can be saved to it, <c>SetupFutureUsage</c>) and, when they chose one, the
/// saved card it is charged on. Both null for a guest or a member who never opened a vault.
/// <para>
/// <c>payments."PaymentCustomers"</c> and <c>payments."SavedPaymentMethods"</c> are under FORCE RLS (ADR-0024, owner
/// decisions 2026-11-07 + 2026-11-15), so both are read in ONE <see cref="PaymentsTenantScope"/> for the ORDER's tenant —
/// the tenant the caller holds (<c>AuthorizePayment.TenantId</c>), never one derived from these tables. A consumer is
/// inside MassTransit's EF outbox transaction, so the scope is applied to that transaction rather than a second one.
/// </para>
/// </summary>
public sealed record CheckoutInstrument(string? ProviderCustomerId, string? ProviderPaymentMethodId)
{
    private static readonly CheckoutInstrument None = new(null, null);

    /// <summary>
    /// The member's stripe vault in <paramref name="tenantId"/> and their chosen active card in it (null when the card
    /// is not theirs, not in this tenant or removed — the intent is then created without it). Nothing without a user.
    /// </summary>
    public static async Task<CheckoutInstrument> FindAsync(
        PaymentsDbContext db, Guid tenantId, Guid? userId, Guid? savedPaymentMethodId, CancellationToken ct)
    {
        if (userId is not { } uid)
        {
            return None;
        }

        return await PaymentsTenantScope.RunAsync(db, tenantId, async () =>
        {
            var customer = await db.PaymentCustomers.AsNoTracking()
                .Where(c => c.TenantId == tenantId && c.UserId == uid && c.Provider == "stripe")
                .Select(c => new { c.Id, c.ProviderCustomerId })
                .SingleOrDefaultAsync(ct);
            if (customer is null)
            {
                return None; // no vault ⇒ no saved card either
            }

            var methodId = savedPaymentMethodId is { } id
                ? await db.SavedPaymentMethods.AsNoTracking()
                    .Where(m => m.Id == id && m.TenantId == tenantId && m.UserId == uid && m.PaymentCustomerId == customer.Id
                        && m.State == SavedPaymentMethodState.Active)
                    .Select(m => m.ProviderPaymentMethodId)
                    .SingleOrDefaultAsync(ct)
                : null;
            return new CheckoutInstrument(customer.ProviderCustomerId, methodId);
        }, ct, uid);
    }
}
