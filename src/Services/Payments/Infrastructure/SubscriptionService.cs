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

        // Copy the instrument the order paid with so renewals can charge it off-session (a saved card or a
        // direct-debit mandate's payment method). Absent (e.g. a legacy order) → renewal falls back to the
        // host default account.
        var instrument = await db.Payments.AsNoTracking()
            .Where(p => p.OrderId == m.OrderId)
            .Select(p => new { p.ProviderCustomerId, p.ProviderPaymentMethodId })
            .FirstOrDefaultAsync(ct);

        var subscription = Subscription.Start(
            m.TenantId, m.OrderId, m.CustomerEmail, m.ProductId, m.VariantId, m.BillingPeriod, m.PriceMinor, m.Currency, clock.GetUtcNow(),
            instrument?.ProviderCustomerId, instrument?.ProviderPaymentMethodId);
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync(ct);
        return subscription;
    }

    public async Task<Subscription?> RenewAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        // Load the history so Renew() appends the next Sequence (n+1), not a duplicate first period.
        var subscription = await db.Subscriptions.Include(s => s.Renewals)
            .SingleOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId, ct);
        if (subscription is null)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        try
        {
            // Charge the renewal period via the rail (the mock returns an intent deterministically). When the
            // subscription carries a stored instrument, charge it MERCHANT-INITIATED (off-session) so the
            // renewal doesn't re-trigger SCA; the provider adapter confirms off-session when a payment method
            // is supplied (StripePaymentProvider sets OffSession/Confirm on a present ProviderPaymentMethodId).
            var account = modeResolver.DefaultAccountForHost();
            await registry.Resolve(account).AuthorizeAsync(
                new PaymentRequest(
                    subscription.OrderId, subscription.PriceMinor, subscription.Currency,
                    $"renew-{subscription.Id}-{subscription.CurrentPeriodEnd:O}", PaymentMethodKind.Card, account,
                    subscription.ProviderCustomerId, subscription.ProviderPaymentMethodId),
                ct);
            subscription.Renew(now);
            // Renew() appended a new client-keyed SubscriptionRenewal to the TRACKED aggregate's nav.
            // DetectChanges infers such a child Modified (UPDATE → 0 rows → DbUpdateConcurrencyException),
            // so add it through the context directly to mark it Added (same trap as StorefrontEndpoints).
            db.SubscriptionRenewals.Add(subscription.Renewals[^1]);
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

    /// <summary>A single subscription with its full renewal history (ordered by Sequence), or null.</summary>
    public async Task<Subscription?> GetAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        var subscription = await db.Subscriptions.AsNoTracking().Include(s => s.Renewals)
            .SingleOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId, ct);
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
