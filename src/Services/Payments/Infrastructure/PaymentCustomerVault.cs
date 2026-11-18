using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Infrastructure;

/// <summary>
/// Finds or opens a member's provider vault (<see cref="PaymentCustomer"/>) — the one way the customer endpoints
/// (<c>/payment-methods</c>, <c>/mandates</c>) reach <c>payments."PaymentCustomers"</c>.
/// <para>
/// The table is under FORCE RLS (ADR-0024, owner decision 2026-11-15), so each read and write runs in the caller's
/// tenant scope (<see cref="PaymentsTenantScope"/>, the request's <c>tenant</c> claim) and commits inside it. The
/// provider call that opens a new vault runs BETWEEN the two scopes, so no database transaction is held open across
/// a network round-trip to the PSP.
/// </para>
/// </summary>
public static class PaymentCustomerVault
{
    /// <summary>
    /// The member's vault on <paramref name="provider"/> in <paramref name="tenantId"/>, created through
    /// <paramref name="openProviderCustomer"/> when there is none. A non-null <paramref name="email"/> is recorded on it
    /// (<see cref="PaymentCustomer.RecordEmail"/>) and saved when it is new or different. The returned row is tracked.
    /// </summary>
    public static async Task<PaymentCustomer> GetOrCreateAsync(
        PaymentsDbContext db,
        Guid tenantId,
        Guid userId,
        string provider,
        string? email,
        Func<CancellationToken, Task<string>> openProviderCustomer,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var existing = await PaymentsTenantScope.RunAsync(db, tenantId, async () =>
        {
            var customer = await db.PaymentCustomers.SingleOrDefaultAsync(
                c => c.TenantId == tenantId && c.UserId == userId && c.Provider == provider, ct);
            if (customer is not null && email is not null && customer.RecordEmail(email))
            {
                await db.SaveChangesAsync(ct);
            }

            return customer;
        }, ct, userId);
        if (existing is not null)
        {
            return existing;
        }

        var providerCustomerId = await openProviderCustomer(ct);
        var created = new PaymentCustomer
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            UserId = userId,
            Provider = provider,
            ProviderCustomerId = providerCustomerId,
            CreatedAt = StoredTime.Of(now),
        };
        if (email is not null)
        {
            created.RecordEmail(email);
        }

        await PaymentsTenantScope.RunAsync(db, tenantId, async () =>
        {
            db.PaymentCustomers.Add(created);
            await db.SaveChangesAsync(ct);
        }, ct, userId);
        return created;
    }
}
