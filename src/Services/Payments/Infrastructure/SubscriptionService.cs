using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;
using ThreeCommerce.Payments.Infrastructure.Providers;

namespace ThreeCommerce.Payments.Infrastructure;

/// <summary>
/// Subscription lifecycle (mt7_3): set up at confirmation (the first period was paid with the order),
/// renew by charging the period via the resolved payment provider, mark past-due on failure, cancel.
/// </summary>
public sealed class SubscriptionService(
    PaymentsDbContext db, IPaymentProviderRegistry registry, PaymentModeResolver modeResolver, TimeProvider clock,
    ILogger<SubscriptionService> logger)
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
            instrument?.ProviderCustomerId, instrument?.ProviderPaymentMethodId, m.StorefrontId, m.TaxInclusive, m.TaxExempt);
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
            var response = await registry.Resolve(account).AuthorizeAsync(
                new PaymentRequest(
                    subscription.OrderId, subscription.PriceMinor, subscription.Currency,
                    $"renew-{subscription.Id}-{subscription.CurrentPeriodEnd:O}", PaymentMethodKind.Card, account,
                    subscription.ProviderCustomerId, subscription.ProviderPaymentMethodId),
                ct);
            subscription.Renew(now);
            // Renew() appended a new client-keyed SubscriptionRenewal to the TRACKED aggregate's nav.
            // DetectChanges infers such a child Modified (UPDATE → 0 rows → DbUpdateConcurrencyException),
            // so add it through the context directly to mark it Added (same trap as StorefrontEndpoints).
            var renewal = subscription.Renewals[^1];
            db.SubscriptionRenewals.Add(renewal);

            // pay_renew_posts_revenue: a settled renewal records revenue in the ledger, attributed to the
            // subscription's storefront (ledger_sf) — before this, renewals charged the card but posted
            // nothing. Only a Succeeded settlement posts; a RequiresAction/Failed outcome does not.
            if (response.Outcome == PaymentOutcome.Succeeded)
            {
                await PostRenewalRevenueAsync(subscription, renewal.Sequence, account.Provider, now, ct);
            }
        }
        catch (Exception ex) when (ex is not SubscriptionRuleException)
        {
            subscription.MarkPastDue(now); // dunning
        }
    }

    /// <summary>
    /// Book the renewal's revenue to the storefront's own ledger accounts (mirrors the order-sale posting in
    /// PaymentEventProcessor): a receivable-bridged Sale for the period price to <c>revenue.store-{id}</c> /
    /// <c>tax.store-{id}</c> / <c>cash.store-{id}.{provider}</c>. The reference <c>renew-{id}-{sequence}</c> is
    /// idempotency-distinct from the order's own sale entry and from other periods, so a redelivered renewal
    /// never double-posts.
    /// <para>
    /// Tax (ADR-0063 P4, renewal_usage_tax_2026_10_11 — before this every renewal posted tax 0, so on an
    /// inclusive store the whole charge landed in revenue and output tax was under-reported): the regime is
    /// the subscription's snapshot (<see cref="Subscription.TaxInclusive"/>, what its price MEANS), the rate is
    /// the storefront's rate in force NOW (rates change by law; the tax point is each period's supply), and the
    /// split is checkout's: <see cref="RecurringChargeTax"/>. The charged gross is unchanged. An exclusive
    /// regime posts no tax and charges the bare price, exactly as before, pending the owner's decision.
    /// </para>
    /// </summary>
    private async Task PostRenewalRevenueAsync(Subscription subscription, int sequence, string provider, DateTimeOffset now, CancellationToken ct)
    {
        var reference = $"renew-{subscription.Id}-{sequence}";
        if (await db.JournalEntries.AnyAsync(e => e.Reference == reference, ct))
        {
            return; // already posted for this period
        }

        var accounts = subscription.StorefrontId is { } sid
            ? await db.StorefrontLedgerAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.StorefrontId == sid, ct)
            : null;
        var tax = RecurringChargeTax.For(
            subscription.PriceMinor, accounts?.TaxRateBasisPoints,
            subscription.TaxInclusive ?? accounts?.TaxInclusive ?? false, subscription.TaxExempt);
        LogUnpostedTax(logger, tax.Basis, reference, subscription.StorefrontId);
        var sale = Ledger.Sale(
            subscription.OrderId, subscription.PriceMinor, tax.TaxMinor, 0, subscription.Currency, now,
            PaymentMethodKind.Card, provider, accounts?.RevenueAccountCode, accounts?.TaxAccountCode, accounts?.ReceivableAccountCode,
            0, accounts?.ShippingAccountCode, subscription.StorefrontId, reference,
            $"Subscription renewal {subscription.Id} period {sequence}");
        if (sale.Lines.Count > 0)
        {
            db.JournalEntries.Add(sale);
        }
    }

    /// <summary>Make a renewal/usage charge that posts NO tax for want of facts visible, rather than silent.</summary>
    internal static void LogUnpostedTax(ILogger logger, RecurringChargeTaxBasis basis, string reference, Guid? storefrontId)
    {
        if (basis == RecurringChargeTaxBasis.Unknown)
        {
            logger.LogWarning(
                "Charge {Reference}: storefront {StorefrontId} has no projected tax rate; posting no tax", reference, storefrontId);
        }
        else if (basis == RecurringChargeTaxBasis.ExclusiveNotCharged)
        {
            logger.LogWarning(
                "Charge {Reference}: storefront {StorefrontId} is tax-exclusive; the charge carries no added tax (pending decision), posting none",
                reference, storefrontId);
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
