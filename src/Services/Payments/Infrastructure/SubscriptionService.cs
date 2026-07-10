using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure.Providers;

namespace ThreeCommerce.Payments.Infrastructure;

/// <summary>
/// Subscription lifecycle (mt7_3): set up at confirmation (the first period was paid with the order),
/// renew by charging the period via the resolved payment provider, mark past-due on failure, cancel.
/// </summary>
public sealed class SubscriptionService(PaymentsDbContext db, IPaymentProviderRegistry registry, PaymentModeResolver modeResolver, TimeProvider clock)
{
    public async Task<Subscription> StartAsync(SubscriptionRequested m, CancellationToken ct)
    {
        var existing = await db.Subscriptions.FirstOrDefaultAsync(
            s => s.OrderId == m.OrderId && s.ProductId == m.ProductId && s.VariantId == m.VariantId, ct);
        if (existing is not null)
        {
            return existing; // idempotent per (order, product, variant)
        }

        var subscription = Subscription.Start(
            m.TenantId, m.OrderId, m.CustomerEmail, m.ProductId, m.VariantId, m.BillingPeriod, m.PriceMinor, m.Currency, clock.GetUtcNow());
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync(ct);
        return subscription;
    }

    public async Task<Subscription?> RenewAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        var subscription = await db.Subscriptions.SingleOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId, ct);
        if (subscription is null)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        try
        {
            // Charge the renewal period via the rail (the mock returns an intent deterministically).
            var account = modeResolver.DefaultAccountForHost();
            await registry.Resolve(account).AuthorizeAsync(
                new PaymentRequest(
                    subscription.OrderId, subscription.PriceMinor, subscription.Currency,
                    $"renew-{subscription.Id}-{subscription.CurrentPeriodEnd:O}", PaymentMethodKind.Card, account),
                ct);
            subscription.Renew(now);
        }
        catch (Exception ex) when (ex is not SubscriptionRuleException)
        {
            subscription.MarkPastDue(now); // dunning
        }

        await db.SaveChangesAsync(ct);
        return subscription;
    }

    public async Task<Subscription?> CancelAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        var subscription = await db.Subscriptions.SingleOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId, ct);
        if (subscription is null)
        {
            return null;
        }

        subscription.Cancel(clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return subscription;
    }

    public Task<List<Subscription>> ListAsync(Guid tenantId, string? email, CancellationToken ct)
    {
        var query = db.Subscriptions.AsNoTracking().Where(s => s.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(email))
        {
            var normalized = email.Trim().ToLowerInvariant();
            query = query.Where(s => s.CustomerEmail == normalized);
        }

        return query.OrderByDescending(s => s.CreatedAt).ToListAsync(ct);
    }
}
