using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Infrastructure;

/// <summary>
/// A customer's DEFAULT saved card, as the instrument an off-session charge is made on (owner decision 2026-11-06,
/// <c>jobmgr_3_settlement</c>: usage auto top-ups charge it).
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
        return (
                from customer in db.PaymentCustomers.AsNoTracking()
                where customer.TenantId == tenantId && customer.Email == normalized
                from method in db.SavedPaymentMethods.AsNoTracking()
                where method.PaymentCustomerId == customer.Id && method.IsDefault && method.State == SavedPaymentMethodState.Active
                orderby method.UpdatedAt descending, method.CreatedAt descending
                select new DefaultSavedCard(customer.ProviderCustomerId, method.ProviderPaymentMethodId))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Whether the customer of this vault has an active default card.</summary>
    public static Task<bool> ExistsForCustomerAsync(PaymentsDbContext db, Guid paymentCustomerId, CancellationToken ct) =>
        db.SavedPaymentMethods.AsNoTracking()
            .AnyAsync(m => m.PaymentCustomerId == paymentCustomerId && m.IsDefault && m.State == SavedPaymentMethodState.Active, ct);
}
