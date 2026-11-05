using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Gives a usage customer a saved card in Payments (owner decision 2026-11-06, jobmgr_3_settlement): an auto top-up charges
/// the customer's DEFAULT saved card and is skipped without one, so a test that expects an auto-load to be charged seeds one.
/// </summary>
internal static class SavedCardSeed
{
    /// <summary>
    /// Ensure the customer (tenant + email) has a vault and an active saved card — the default unless
    /// <paramref name="isDefault"/> is false, and with the vault's email recorded unless <paramref name="recordEmail"/> is
    /// false (a vault created before the email was kept). Idempotent per tenant + email + card. Returns (user, card id).
    /// </summary>
    public static async Task<(Guid UserId, Guid CardId)> AddCardAsync(
        IServiceProvider payments, Guid tenantId, string email, string pm = "pm_seed_default", bool isDefault = true, bool recordEmail = true)
    {
        await using var scope = payments.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var normalized = email.Trim().ToLowerInvariant();
        var customerId = $"cus_seed_{tenantId:N}_{normalized}";
        var customer = await db.PaymentCustomers.SingleOrDefaultAsync(c => c.ProviderCustomerId == customerId);
        if (customer is null)
        {
            customer = new PaymentCustomer
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                UserId = Guid.CreateVersion7(),
                Provider = "stripe",
                ProviderCustomerId = customerId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.PaymentCustomers.Add(customer);
        }

        if (recordEmail)
        {
            customer.RecordEmail(normalized);
        }

        var card = await db.SavedPaymentMethods.SingleOrDefaultAsync(m => m.PaymentCustomerId == customer.Id && m.ProviderPaymentMethodId == pm);
        if (card is null)
        {
            card = new SavedPaymentMethod
            {
                Id = Guid.CreateVersion7(),
                PaymentCustomerId = customer.Id,
                TenantId = tenantId,
                UserId = customer.UserId,
                Provider = "stripe",
                ProviderPaymentMethodId = pm,
                Brand = "visa",
                Last4 = "4242",
                ExpMonth = 12,
                ExpYear = DateTimeOffset.UtcNow.Year + 3,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.SavedPaymentMethods.Add(card);
        }

        if (isDefault && !card.IsDefault)
        {
            card.MakeDefault(DateTimeOffset.UtcNow);
        }

        await db.SaveChangesAsync();
        return (customer.UserId, card.Id);
    }
}
