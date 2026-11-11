using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Tenancy;
using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Infrastructure;

/// <summary>
/// A customer's DEFAULT saved card, as the instrument an off-session charge is made on (owner decision 2026-11-06,
/// <c>jobmgr_3_settlement</c>: usage auto top-ups charge it).
/// <para>
/// Saved cards are tenant-owned and go under FORCE RLS (owner decision 2026-11-07, ADR-0024), so every read here runs
/// inside the charge's TENANT scope (<see cref="TenantDatabaseExtensions.RunInTenantScopeAsync{T}"/>) — a message
/// consumer and a job have no request scope to inherit. Inside a transaction the caller already opened, the read joins it
/// (that caller owns the scope). A non-relational provider (the unit tests' InMemory) has neither transactions nor RLS.
/// </para>
/// </summary>
public sealed record DefaultSavedCard(string ProviderCustomerId, string ProviderPaymentMethodId)
{
    /// <summary>
    /// The active default card of the customer with this email in this tenant (the email the customer's vault was last
    /// managed under, <see cref="PaymentCustomer.Email"/>), or null — no vault, no card, or no card marked default (a
    /// non-default card is never charged in its place).
    /// </summary>
    public static Task<DefaultSavedCard?> FindAsync(PaymentsDbContext db, Guid tenantId, string email, CancellationToken ct)
    {
        var normalized = email.Trim().ToLowerInvariant();
        return InTenantScopeAsync(db, tenantId, () => (
                from customer in db.PaymentCustomers.AsNoTracking()
                where customer.TenantId == tenantId && customer.Email == normalized
                from method in db.SavedPaymentMethods.AsNoTracking()
                where method.TenantId == tenantId && method.PaymentCustomerId == customer.Id
                    && method.IsDefault && method.State == SavedPaymentMethodState.Active
                orderby method.UpdatedAt descending, method.CreatedAt descending
                select new DefaultSavedCard(customer.ProviderCustomerId, method.ProviderPaymentMethodId))
            .FirstOrDefaultAsync(ct), ct);
    }

    /// <summary>Whether the customer of this vault (in its tenant) has an active default card.</summary>
    public static Task<bool> ExistsForCustomerAsync(PaymentsDbContext db, Guid tenantId, Guid paymentCustomerId, CancellationToken ct) =>
        InTenantScopeAsync(db, tenantId, () => db.SavedPaymentMethods.AsNoTracking()
            .AnyAsync(m => m.TenantId == tenantId && m.PaymentCustomerId == paymentCustomerId && m.IsDefault
                && m.State == SavedPaymentMethodState.Active, ct), ct);

    private static Task<T> InTenantScopeAsync<T>(PaymentsDbContext db, Guid tenantId, Func<Task<T>> read, CancellationToken ct) =>
        !db.Database.IsRelational() || db.Database.CurrentTransaction is not null
            ? read()
            : db.RunInTenantScopeAsync(TenantContext.ForTenant(tenantId), read, ct);
}
