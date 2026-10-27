using MassTransit;
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
/// <paramref name="publisher"/> is the scoped publish endpoint (bus outbox): what it publishes commits with the
/// renewal's own SaveChanges.
/// </summary>
public sealed class SubscriptionService(
    PaymentsDbContext db, IPaymentProviderRegistry registry, PaymentModeResolver modeResolver, TimeProvider clock,
    ILogger<SubscriptionService> logger, IPublishEndpoint publisher)
{
    public async Task<Subscription> StartAsync(SubscriptionRequested m, CancellationToken ct)
    {
        var existing = await db.Subscriptions.FirstOrDefaultAsync(
            s => s.OrderId == m.OrderId && s.ProductId == m.ProductId && s.VariantId == m.VariantId, ct);
        if (existing is not null)
        {
            // Idempotent per (order, product, variant). The one exception is the quantity backfill
            // (subscription_quantity_backfill): a subscription set up before quantity was carried is stored as one unit at
            // one unit's price; the re-sent request carries the line's quantity and its correct renewal price.
            if (existing.CorrectLegacyQuantity(m.Quantity, m.PriceMinor, clock.GetUtcNow()))
            {
                logger.LogInformation(
                    "Subscription {SubscriptionId} re-priced for {Quantity} units: renewals now charge {PriceMinor} {Currency} (quantity backfill)",
                    existing.Id, existing.Quantity, existing.PriceMinor, existing.Currency);
                await db.SaveChangesAsync(ct);
            }

            return existing;
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
            instrument?.ProviderCustomerId, instrument?.ProviderPaymentMethodId, m.StorefrontId, m.TaxInclusive, m.TaxExempt,
            Math.Max(1, m.Quantity)); // an older message omits Quantity (default 1); never fewer than one unit
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
                // Committed now, deliberately: every renewal below commits on its own (see ChargeRenewalAsync), and
                // nothing else may be left pending to ride along with one of those commits.
                await db.SaveChangesAsync(ct);
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

    /// <summary>
    /// Charge one renewal period off-session and advance the aggregate; dun to PastDue on failure — and COMMIT it.
    /// <para>
    /// Each renewal is its own unit of work, deliberately (ADR-0057 amendment, 2026-10-15): the provider call moves
    /// real money, so the period's outcome (advanced + history row + ledger entry, or PastDue) is made durable BEFORE
    /// the next customer's card is touched. A crash or DB failure later in a nightly run can then lose at most the
    /// renewal in flight — whose pinned key + amount (<see cref="RecurringChargePin"/>, its own commit before the
    /// provider call) make a re-run de-duplicate at the provider rather than charge twice — instead of rolling back
    /// the record of charges that already happened. The commit sits outside the dunning catch: a failure to record a
    /// SUCCESSFUL charge must abort the run (and be retried under the same key), never dun a customer who paid.
    /// </para>
    /// </summary>
    private async Task ChargeRenewalAsync(Subscription subscription, DateTimeOffset now, CancellationToken ct)
    {
        await ChargeAndAdvanceAsync(subscription, now, ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task ChargeAndAdvanceAsync(Subscription subscription, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            // The period's tax is resolved BEFORE the charge, because on an exclusive (US) store it changes what is
            // charged: price + checkout's add-on tax (owner decision 2026-10-15, option A). Inclusive / 0% / exempt /
            // unknown-rate charge the price itself (see RenewalTax). The amount is PINNED to the period's
            // idempotency key (persisted before the provider call): a retry of the same period — after a failure,
            // a crash, or a rate change in between — re-sends the SAME amount under the SAME key, which a provider
            // de-duplicates instead of rejecting (Stripe idempotency_error) or charging twice.
            var idempotencyKey = $"renew-{subscription.Id}-{subscription.CurrentPeriodEnd:O}";
            var accounts = await AccountsAsync(subscription.StorefrontId, ct);
            var tax = await RecurringChargePin.PinAsync(db, clock, idempotencyKey, () => RenewalTax(subscription, accounts), ct);

            // When the subscription carries a stored instrument, charge it MERCHANT-INITIATED (off-session) so
            // the renewal doesn't re-trigger SCA; the provider adapter confirms off-session when a payment
            // method is supplied (StripePaymentProvider sets OffSession/Confirm on a present method).
            var account = modeResolver.DefaultAccountForHost();
            var response = await registry.Resolve(account).AuthorizeAsync(
                new PaymentRequest(
                    subscription.OrderId, tax.ChargeMinor, subscription.Currency,
                    idempotencyKey, PaymentMethodKind.Card, account,
                    subscription.ProviderCustomerId, subscription.ProviderPaymentMethodId),
                ct);

            // Only a SETTLED charge renews (owner decision 2026-10-27, ADR-0057 amendment). A Failed or
            // RequiresAction outcome (and a Cancelled one) took no money, so it goes down the same dunning path as a
            // thrown provider error: PastDue, the period does NOT advance (so a retry is the same period — same key,
            // same pinned amount), no history row, no revenue, no tax. RequiresAction additionally asks the customer
            // to authenticate.
            if (response.Outcome != PaymentOutcome.Succeeded)
            {
                await DunAsync(subscription, response, tax, now, ct);
                return;
            }

            subscription.Renew(now, tax.ChargeMinor);
            // Renew() appended a new client-keyed SubscriptionRenewal to the TRACKED aggregate's nav.
            // DetectChanges infers such a child Modified (UPDATE → 0 rows → DbUpdateConcurrencyException),
            // so add it through the context directly to mark it Added (same trap as StorefrontEndpoints).
            var renewal = subscription.Renewals[^1];
            db.SubscriptionRenewals.Add(renewal);

            // pay_renew_posts_revenue: a settled renewal records revenue in the ledger, attributed to the
            // subscription's storefront (ledger_sf) — before this, renewals charged the card but posted nothing.
            await PostRenewalRevenueAsync(subscription, renewal.Sequence, tax, accounts, account.Provider, now, ct);
        }
        catch (Exception ex) when (ex is not SubscriptionRuleException)
        {
            subscription.MarkPastDue(now); // dunning
        }
    }

    /// <summary>
    /// A renewal the provider did not settle (Failed / RequiresAction / Cancelled): dun it exactly like a thrown provider
    /// error — PastDue, period not advanced, nothing posted. A RequiresAction outcome also publishes
    /// <see cref="SubscriptionRenewalActionRequired"/> (bus outbox: it commits with the dunning, or not at all) so
    /// Notifications asks the customer to authenticate the payment.
    /// </summary>
    private async Task DunAsync(
        Subscription subscription, PaymentResponse response, RecurringChargeTaxResult tax, DateTimeOffset now, CancellationToken ct)
    {
        subscription.MarkPastDue(now);
        logger.LogWarning(
            "Renewal of subscription {SubscriptionId} for period ending {PeriodEnd:O} was not settled ({Outcome}, {ErrorCode}); marked past-due, period not advanced",
            subscription.Id, subscription.CurrentPeriodEnd, response.Outcome, response.Error?.Code);
        if (response.Outcome == PaymentOutcome.RequiresAction)
        {
            await publisher.Publish(new SubscriptionRenewalActionRequired(
                subscription.TenantId, subscription.Id, subscription.StorefrontId, subscription.CustomerEmail,
                tax.ChargeMinor, subscription.Currency, subscription.CurrentPeriodEnd), ct);
        }
    }

    /// <summary>
    /// Book the renewal's revenue to the storefront's own ledger accounts (mirrors the order-sale posting in
    /// PaymentEventProcessor): a receivable-bridged Sale for the period price to <c>revenue.store-{id}</c> /
    /// <c>tax.store-{id}</c> / <c>cash.store-{id}.{provider}</c>. The reference <c>renew-{id}-{sequence}</c> is
    /// idempotency-distinct from the order's own sale entry and from other periods, so a redelivered renewal
    /// never double-posts.
    /// <para>
    /// Tax (ADR-0063 P4): the regime is the subscription's snapshot (<see cref="Subscription.TaxInclusive"/>,
    /// what its price MEANS), the rate is the storefront's rate in force NOW (rates change by law; the tax point
    /// is each period's supply), and the arithmetic is checkout's (<see cref="RecurringChargeTax"/>), resolved
    /// before the charge by <see cref="RenewalTax"/> and pinned per period (<see cref="RecurringChargePin"/>). Inclusive: net + contained tax, gross = the price.
    /// Exclusive (owner decision 2026-10-15, option A): net = the price, tax = the added tax, gross = what was
    /// charged (price + tax) — the same split checkout posts for the first period.
    /// </para>
    /// </summary>
    private async Task PostRenewalRevenueAsync(
        Subscription subscription, int sequence, RecurringChargeTaxResult tax, StorefrontLedgerAccounts? accounts,
        string provider, DateTimeOffset now, CancellationToken ct)
    {
        var reference = $"renew-{subscription.Id}-{sequence}";
        if (await db.JournalEntries.AnyAsync(e => e.Reference == reference, ct))
        {
            return; // already posted for this period
        }

        var sale = Ledger.Sale(
            subscription.OrderId, tax.ChargeMinor, tax.TaxMinor, 0, subscription.Currency, now,
            PaymentMethodKind.Card, provider, accounts?.RevenueAccountCode, accounts?.TaxAccountCode, accounts?.ReceivableAccountCode,
            0, accounts?.ShippingAccountCode, subscription.StorefrontId, reference,
            $"Subscription renewal {subscription.Id} period {sequence}");
        if (sale.Lines.Count > 0)
        {
            db.JournalEntries.Add(sale);
        }
    }

    /// <summary>The storefront's projected ledger accounts + tax terms (null for an unattributed subscription).</summary>
    private async Task<StorefrontLedgerAccounts?> AccountsAsync(Guid? storefrontId, CancellationToken ct) =>
        storefrontId is { } sid
            ? await db.StorefrontLedgerAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.StorefrontId == sid, ct)
            : null;

    /// <summary>
    /// The tax of the period about to be charged, at the store's rate in force NOW — computed only on the first attempt
    /// of a period (then pinned, <see cref="RecurringChargePin"/>). Logged when the rate is unknown (<see cref="LogUnknownRate"/>).
    /// </summary>
    private RecurringChargeTaxResult RenewalTax(Subscription subscription, StorefrontLedgerAccounts? accounts)
    {
        var taxInclusive = subscription.TaxInclusive ?? accounts?.TaxInclusive ?? false;
        var tax = RecurringChargeTax.For(subscription.PriceMinor, accounts?.TaxRateBasisPoints, taxInclusive, subscription.TaxExempt);
        LogUnknownRate(logger, tax, taxInclusive, $"renew-{subscription.Id}", subscription.StorefrontId);
        return tax;
    }

    /// <summary>
    /// A renewal/usage charge whose storefront rate is not projected (a projection written before the rate was
    /// carried, or no storefront) charges the BARE price and posts no tax — never a guessed rate, and never a
    /// skipped or dunned renewal for want of our own read model (owner decision 2026-10-15). On an exclusive store
    /// that under-collects the tax the merchant owes, so it is logged as an error; on an inclusive store only the
    /// split is missing (the gross is right), a warning. It heals as soon as the store's config is re-published
    /// (saving the storefront in admin re-sends StorefrontConfigChanged).
    /// </summary>
    internal static void LogUnknownRate(ILogger logger, RecurringChargeTaxResult tax, bool taxInclusive, string reference, Guid? storefrontId)
    {
        if (tax.Basis != RecurringChargeTaxBasis.Unknown)
        {
            return;
        }

        if (taxInclusive)
        {
            logger.LogWarning(
                "Charge {Reference}: storefront {StorefrontId} has no projected tax rate; charging {ChargeMinor} and posting no tax split",
                reference, storefrontId, tax.ChargeMinor);
        }
        else
        {
            logger.LogError(
                "Charge {Reference}: storefront {StorefrontId} has no projected tax rate; charging the bare price {ChargeMinor} WITHOUT the added tax (under-collected) — re-publish the storefront config",
                reference, storefrontId, tax.ChargeMinor);
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
