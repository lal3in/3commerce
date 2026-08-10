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
            instrument?.ProviderCustomerId, instrument?.ProviderPaymentMethodId, m.StorefrontId);
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

        await ChargeRenewalAsync(subscription, clock.GetUtcNow(), ct);
        await db.SaveChangesAsync(ct);
        return subscription;
    }

    /// <summary>
    /// Auto-renew due subscriptions per storefront (jobmgr_2). Runs on a fine cadence; for each storefront
    /// with a due subscription, once its configured daily time has passed and it hasn't run today, renews
    /// every Active/Trialing subscription whose period has ended, then stamps the schedule so it fires at most
    /// once per storefront per day. Idempotent. Returns the number of subscriptions renewed.
    /// </summary>
    public async Task<int> AutoRenewDueAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var timeOfDay = TimeOnly.FromDateTime(now.UtcDateTime);

        var dueStorefronts = await db.Subscriptions.AsNoTracking()
            .Where(s => s.StorefrontId != null && s.CurrentPeriodEnd <= now
                && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trialing))
            .Select(s => s.StorefrontId!.Value)
            .Distinct()
            .ToListAsync(ct);

        var renewed = 0;
        foreach (var storefrontId in dueStorefronts)
        {
            var schedule = await db.StorefrontBillingSchedules.SingleOrDefaultAsync(x => x.StorefrontId == storefrontId, ct);
            if (schedule is null)
            {
                schedule = StorefrontBillingSchedule.CreateDefault(storefrontId, now);
                db.StorefrontBillingSchedules.Add(schedule);
            }

            if (!schedule.ShouldRunNow(today, timeOfDay))
            {
                continue;
            }

            var due = await db.Subscriptions.Include(s => s.Renewals)
                .Where(s => s.StorefrontId == storefrontId && s.CurrentPeriodEnd <= now
                    && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trialing))
                .ToListAsync(ct);
            foreach (var subscription in due)
            {
                await ChargeRenewalAsync(subscription, now, ct);
                renewed++;
            }

            schedule.MarkRan(today, now);
        }

        await db.SaveChangesAsync(ct);
        return renewed;
    }

    /// <summary>Charge one renewal period off-session and advance the aggregate; dun to PastDue on failure.
    /// The caller owns SaveChanges so a batch sweep commits all renewals in one unit of work.</summary>
    private async Task ChargeRenewalAsync(Subscription subscription, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            // When the subscription carries a stored instrument, charge it MERCHANT-INITIATED (off-session) so
            // the renewal doesn't re-trigger SCA; the provider adapter confirms off-session when a payment
            // method is supplied (StripePaymentProvider sets OffSession/Confirm on a present method).
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
