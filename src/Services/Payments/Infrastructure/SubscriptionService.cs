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

    /// <summary>
    /// An operator's Renew (admin): charge the subscription's current period now — the first attempt of a due period,
    /// or another attempt of a past-due one (a fresh attempt key, the same pinned amount). <paramref name="scenario"/> is
    /// honoured by the LocalMock rail only (the dev endpoint that drives a past-due renewal in E2E).
    /// </summary>
    public async Task<Subscription?> RenewAsync(Guid tenantId, Guid id, CancellationToken ct, MockScenario? scenario = null)
    {
        // Load the history so Renew() appends the next Sequence (n+1), not a duplicate first period.
        var subscription = await db.Subscriptions.Include(s => s.Renewals)
            .SingleOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId, ct);
        if (subscription is null)
        {
            return null;
        }

        await ChargeRenewalAsync(subscription, clock.GetUtcNow(), SubscriptionChargeTrigger.Operator, ct, scenario: scenario);
        await db.SaveChangesAsync(ct);
        return subscription;
    }

    /// <summary>
    /// Auto-renew due subscriptions per storefront (jobmgr_2). Runs on a fine cadence; for each storefront with a due
    /// subscription or a due dunning retry, once its configured daily time has passed and it hasn't run today, renews
    /// every Active/Trialing subscription whose period has ended and retries every PastDue one whose scheduled retry
    /// (days 1, 3 and 7 after the first failure — <see cref="Subscription.ScheduledRetryDue"/>) has come, cancelling
    /// it when the final retry does not settle; then stamps the schedule so it fires at most once per storefront per
    /// day. Idempotent. Returns the number of subscriptions charged.
    /// </summary>
    public async Task<int> AutoRenewDueAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var timeOfDay = TimeOnly.FromDateTime(now.UtcDateTime);
        var firstRetry = now.AddDays(-Subscription.RetryDays[0]);

        var dueStorefronts = await db.Subscriptions.AsNoTracking()
            .Where(s => s.StorefrontId != null
                && ((s.CurrentPeriodEnd <= now && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trialing))
                    || (s.Status == SubscriptionStatus.PastDue && s.DunningStartedAt != null && s.DunningStartedAt <= firstRetry)))
            .Select(s => s.StorefrontId!.Value)
            .Distinct()
            .ToListAsync(ct);

        var charged = 0;
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
                await ChargeRenewalAsync(subscription, now, SubscriptionChargeTrigger.Renewal, ct);
                charged++;
            }

            var pastDue = await db.Subscriptions.Include(s => s.Renewals)
                .Where(s => s.StorefrontId == storefrontId && s.Status == SubscriptionStatus.PastDue
                    && s.DunningStartedAt != null && s.DunningStartedAt <= firstRetry)
                .ToListAsync(ct);
            foreach (var subscription in pastDue)
            {
                if (subscription.ScheduledRetryDue(now) is { } retry)
                {
                    await ChargeRenewalAsync(subscription, now, SubscriptionChargeTrigger.ScheduledRetry, ct, finalRetry: retry.IsFinal);
                    charged++;
                }
            }

            schedule.MarkRan(today, now);
        }

        await db.SaveChangesAsync(ct);
        return charged;
    }

    /// <summary>
    /// The customer's own attempt from the storefront "complete payment" page (owner decision 2026-10-27): charge the
    /// past-due period now, ON-SESSION (the customer is present, so the bank may ask for 3-D Secure), optionally on
    /// another of their saved cards — which then becomes the card future renewals charge. A fresh attempt key, the
    /// period's pinned amount. Returns null when the subscription is not the customer's.
    /// </summary>
    public async Task<SubscriptionPayResult?> PayAsync(
        Guid tenantId, string email, Guid userId, Guid id, Guid? savedPaymentMethodId, CancellationToken ct, MockScenario? scenario = null)
    {
        var normalized = email.Trim().ToLowerInvariant();
        var subscription = await db.Subscriptions.Include(s => s.Renewals)
            .SingleOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId && s.CustomerEmail == normalized, ct);
        if (subscription is null)
        {
            return null;
        }

        if (subscription.Status != SubscriptionStatus.PastDue)
        {
            return new SubscriptionPayResult(subscription, SubscriptionPayOutcome.NotPastDue, null, null);
        }

        var now = clock.GetUtcNow();
        if (savedPaymentMethodId is { } methodId)
        {
            var method = await db.SavedPaymentMethods.AsNoTracking()
                .Where(m => m.Id == methodId && m.UserId == userId && m.TenantId == tenantId && m.State == SavedPaymentMethodState.Active)
                .Select(m => new
                {
                    m.ProviderPaymentMethodId,
                    ProviderCustomerId = db.PaymentCustomers.Where(c => c.Id == m.PaymentCustomerId).Select(c => c.ProviderCustomerId).FirstOrDefault(),
                })
                .SingleOrDefaultAsync(ct);
            if (method is null)
            {
                return new SubscriptionPayResult(subscription, SubscriptionPayOutcome.UnknownPaymentMethod, null, null);
            }

            subscription.ChangeInstrument(method.ProviderCustomerId, method.ProviderPaymentMethodId, now);
        }

        var response = await ChargeRenewalAsync(subscription, now, SubscriptionChargeTrigger.Customer, ct, scenario: scenario);
        var outcome = response?.Outcome switch
        {
            PaymentOutcome.Succeeded => SubscriptionPayOutcome.Paid,
            PaymentOutcome.RequiresAction => SubscriptionPayOutcome.RequiresAction,
            null => SubscriptionPayOutcome.ProviderUnavailable,
            _ => SubscriptionPayOutcome.Declined,
        };
        return new SubscriptionPayResult(
            subscription, outcome, response?.PaymentIntentId, outcome == SubscriptionPayOutcome.RequiresAction ? response?.ClientSecret : null);
    }

    /// <summary>
    /// A provider event says an attempt's PaymentIntent succeeded later (the customer finished 3-D Secure): settle that
    /// period exactly once — renew, history row, the pinned split posted. The caller (the webhook processor) owns the
    /// SaveChanges. Returns false when the intent is not a renewal attempt.
    /// </summary>
    public async Task<bool> SettleAttemptAsync(string paymentIntentId, CancellationToken ct)
    {
        var attempt = await db.SubscriptionChargeAttempts.SingleOrDefaultAsync(a => a.PaymentIntentId == paymentIntentId, ct);
        if (attempt is null)
        {
            return false;
        }

        if (attempt.SettledAt is not null)
        {
            return true; // already settled (the immediate answer, or an earlier delivery of the event)
        }

        var subscription = await db.Subscriptions.Include(s => s.Renewals).SingleAsync(s => s.Id == attempt.SubscriptionId, ct);
        if (subscription.Status == SubscriptionStatus.Cancelled || subscription.CurrentPeriodEnd != attempt.PeriodEnd)
        {
            // Money arrived for a period that is closed (cancelled for non-payment, or settled by another attempt): it
            // must be refunded by an operator — never silently booked against the wrong period.
            logger.LogError(
                "Renewal attempt {Key} of subscription {SubscriptionId} succeeded at the provider but its period is no longer open ({Status}); refund intent {Intent} manually",
                attempt.IdempotencyKey, subscription.Id, subscription.Status, paymentIntentId);
            attempt.Outcome = nameof(PaymentOutcome.Succeeded);
            return true;
        }

        var now = clock.GetUtcNow();
        var accounts = await AccountsAsync(subscription.StorefrontId, ct);
        var tax = await RecurringChargePin.PinAsync(db, clock, subscription.PeriodKey, () => RenewalTax(subscription, accounts), ct);
        await RenewAndPostAsync(subscription, tax, accounts, modeResolver.DefaultAccountForHost().Provider, now, ct);
        attempt.Outcome = nameof(PaymentOutcome.Succeeded);
        attempt.SettledAt = now;
        return true;
    }

    /// <summary>
    /// Charge one renewal period and advance the aggregate; dun to PastDue when it does not settle — and COMMIT it.
    /// <para>
    /// Each renewal is its own unit of work, deliberately (ADR-0057 amendment, 2026-10-15): the provider call moves
    /// real money, so the period's outcome (advanced + history row + ledger entry, or PastDue) is made durable BEFORE
    /// the next customer's card is touched. The attempt (its key) and the period's pinned amount
    /// (<see cref="RecurringChargePin"/>) are committed BEFORE the provider call, so a crash or a thrown provider error
    /// leaves an UNANSWERED attempt whose key the next attempt reuses — the provider then de-duplicates rather than
    /// charging twice. Only an attempt the provider ANSWERED (Failed / RequiresAction / Cancelled) is followed by a
    /// fresh key (<c>{PeriodKey}-a{n}</c>, owner decision 2026-10-27), because a provider replays a reused key's result.
    /// The commit sits outside the dunning catch: a failure to record a SUCCESSFUL charge must abort the run (and be
    /// retried under the same key), never dun a customer who paid.
    /// </para>
    /// </summary>
    private async Task<PaymentResponse?> ChargeRenewalAsync(
        Subscription subscription, DateTimeOffset now, SubscriptionChargeTrigger trigger, CancellationToken ct,
        bool finalRetry = false, MockScenario? scenario = null)
    {
        var response = await ChargeAndAdvanceAsync(subscription, now, trigger, finalRetry, scenario, ct);
        await db.SaveChangesAsync(ct);
        return response;
    }

    private async Task<PaymentResponse?> ChargeAndAdvanceAsync(
        Subscription subscription, DateTimeOffset now, SubscriptionChargeTrigger trigger, bool finalRetry, MockScenario? scenario,
        CancellationToken ct)
    {
        try
        {
            // The period's tax is resolved BEFORE the charge, because on an exclusive (US) store it changes what is
            // charged: price + checkout's add-on tax (owner decision 2026-10-15, option A). Inclusive / 0% / exempt /
            // unknown-rate charge the price itself (see RenewalTax). The amount is PINNED to the PERIOD (persisted
            // before the first provider call): every attempt of the period — a retry after a failure, a crash, or a
            // rate change in between — charges the SAME amount.
            var accounts = await AccountsAsync(subscription.StorefrontId, ct);
            var tax = await RecurringChargePin.PinAsync(db, clock, subscription.PeriodKey, () => RenewalTax(subscription, accounts), ct);
            var attempt = await BeginAttemptAsync(subscription, trigger, tax, now, ct);

            // When the subscription carries a stored instrument, charge it MERCHANT-INITIATED (off-session) so the
            // renewal doesn't re-trigger SCA — unless the customer is present on the pay page, where the bank may ask
            // for 3-D Secure (CustomerPresent → on-session).
            var account = modeResolver.DefaultAccountForHost();
            var response = await registry.Resolve(account).AuthorizeAsync(
                new PaymentRequest(
                    subscription.OrderId, tax.ChargeMinor, subscription.Currency,
                    attempt.IdempotencyKey, PaymentMethodKind.Card, account,
                    subscription.ProviderCustomerId, subscription.ProviderPaymentMethodId,
                    Scenario: scenario, CustomerPresent: trigger == SubscriptionChargeTrigger.Customer),
                ct);
            attempt.PaymentIntentId = response.PaymentIntentId;
            attempt.Outcome = response.Outcome.ToString();
            subscription.AttemptAnswered(now);

            // Only a SETTLED charge renews (owner decision 2026-10-27, ADR-0057 amendment). A Failed or RequiresAction
            // outcome (and a Cancelled one) took no money, so it goes down the dunning path: PastDue, the period does
            // NOT advance, no history row, no revenue, no tax.
            if (response.Outcome != PaymentOutcome.Succeeded)
            {
                await DunAsync(subscription, response, tax, trigger, finalRetry, now, ct);
                return response;
            }

            await RenewAndPostAsync(subscription, tax, accounts, account.Provider, now, ct);
            attempt.SettledAt = now;
            return response;
        }
        catch (Exception ex) when (ex is not SubscriptionRuleException)
        {
            // Unanswered (the provider threw, or something before it did): dunning, but no customer-visible failure —
            // no email; the attempt stays reusable (same key) and does not use up a scheduled retry.
            logger.LogWarning(ex, "Renewal of subscription {SubscriptionId} could not be charged; marked past-due", subscription.Id);
            subscription.MarkPastDue(now, SubscriptionChargeOutcomes.Error);
            return null;
        }
    }

    /// <summary>
    /// The attempt this charge is made under, COMMITTED before the provider is asked: the period's last attempt again
    /// when the provider never answered it (same key — the provider de-duplicates), else a new one with a fresh key.
    /// </summary>
    private async Task<SubscriptionChargeAttempt> BeginAttemptAsync(
        Subscription subscription, SubscriptionChargeTrigger trigger, RecurringChargeTaxResult tax, DateTimeOffset now, CancellationToken ct)
    {
        var last = await db.SubscriptionChargeAttempts
            .Where(a => a.SubscriptionId == subscription.Id && a.PeriodEnd == subscription.CurrentPeriodEnd)
            .OrderByDescending(a => a.Attempt)
            .FirstOrDefaultAsync(ct);
        if (last is { Outcome: SubscriptionChargeOutcomes.Pending })
        {
            return last;
        }

        var attempt = new SubscriptionChargeAttempt
        {
            Id = Guid.CreateVersion7(),
            TenantId = subscription.TenantId,
            SubscriptionId = subscription.Id,
            PeriodEnd = subscription.CurrentPeriodEnd,
            Attempt = subscription.AttemptCount,
            IdempotencyKey = subscription.NextAttemptKey,
            Trigger = trigger,
            AmountMinor = tax.ChargeMinor,
            Currency = subscription.Currency,
            Outcome = SubscriptionChargeOutcomes.Pending,
            CreatedAt = now,
        };
        subscription.KeyIssued(now);
        db.SubscriptionChargeAttempts.Add(attempt);
        await db.SaveChangesAsync(ct);
        return attempt;
    }

    private async Task RenewAndPostAsync(
        Subscription subscription, RecurringChargeTaxResult tax, StorefrontLedgerAccounts? accounts, string provider, DateTimeOffset now,
        CancellationToken ct)
    {
        subscription.Renew(now, tax.ChargeMinor);
        // Renew() appended a new client-keyed SubscriptionRenewal to the TRACKED aggregate's nav.
        // DetectChanges infers such a child Modified (UPDATE → 0 rows → DbUpdateConcurrencyException),
        // so add it through the context directly to mark it Added (same trap as StorefrontEndpoints).
        var renewal = subscription.Renewals[^1];
        db.SubscriptionRenewals.Add(renewal);

        // pay_renew_posts_revenue: a settled renewal records revenue in the ledger, attributed to the
        // subscription's storefront (ledger_sf) — before this, renewals charged the card but posted nothing.
        await PostRenewalRevenueAsync(subscription, renewal.Sequence, tax, accounts, provider, now, ct);
    }

    /// <summary>
    /// A renewal attempt the provider answered without settling (Failed / RequiresAction / Cancelled): PastDue, period
    /// not advanced, nothing posted — and the customer is told (bus outbox: the event commits with the dunning, or not
    /// at all), unless they are on the pay page and see the answer there. RequiresAction asks them to authenticate
    /// (<see cref="SubscriptionRenewalActionRequired"/>); a decline says when it is retried
    /// (<see cref="SubscriptionPaymentFailed"/>); the FINAL scheduled retry not settling cancels the subscription
    /// (<see cref="SubscriptionCancelledForNonPayment"/>).
    /// </summary>
    private async Task DunAsync(
        Subscription subscription, PaymentResponse response, RecurringChargeTaxResult tax, SubscriptionChargeTrigger trigger,
        bool finalRetry, DateTimeOffset now, CancellationToken ct)
    {
        subscription.MarkPastDue(now, response.Outcome.ToString());
        logger.LogWarning(
            "Renewal of subscription {SubscriptionId} for period ending {PeriodEnd:O} was not settled ({Outcome}, {ErrorCode}, {Trigger}); marked past-due, period not advanced",
            subscription.Id, subscription.CurrentPeriodEnd, response.Outcome, response.Error?.Code, trigger);

        if (finalRetry)
        {
            subscription.Cancel(now);
            logger.LogWarning("Subscription {SubscriptionId} cancelled for non-payment after its final retry", subscription.Id);
            await publisher.Publish(new SubscriptionCancelledForNonPayment(
                subscription.TenantId, subscription.Id, subscription.StorefrontId, subscription.CustomerEmail,
                tax.ChargeMinor, subscription.Currency, subscription.CurrentPeriodEnd), ct);
            return;
        }

        if (trigger == SubscriptionChargeTrigger.Customer)
        {
            return; // the customer is on the pay page and sees the answer there
        }

        if (response.Outcome == PaymentOutcome.RequiresAction)
        {
            await publisher.Publish(new SubscriptionRenewalActionRequired(
                subscription.TenantId, subscription.Id, subscription.StorefrontId, subscription.CustomerEmail,
                tax.ChargeMinor, subscription.Currency, subscription.CurrentPeriodEnd, subscription.NextRetryAt, subscription.CancelsAfter), ct);
        }
        else
        {
            await publisher.Publish(new SubscriptionPaymentFailed(
                subscription.TenantId, subscription.Id, subscription.StorefrontId, subscription.CustomerEmail,
                tax.ChargeMinor, subscription.Currency, subscription.CurrentPeriodEnd, subscription.NextRetryAt, subscription.CancelsAfter), ct);
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

    /// <summary>The signed-in customer's own subscription (tenant + email claims), or null — 404, never 403.</summary>
    public Task<Subscription?> GetForCustomerAsync(Guid tenantId, string email, Guid id, CancellationToken ct)
    {
        var normalized = email.Trim().ToLowerInvariant();
        return db.Subscriptions.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId && s.CustomerEmail == normalized, ct);
    }

    /// <summary>What the current period charges — the pinned amount (price + any added tax) once it has been attempted.</summary>
    public async Task<long?> AmountDueAsync(Subscription subscription, CancellationToken ct) =>
        (await RecurringChargePin.PinnedAsync(db, subscription.PeriodKey, ct))?.ChargeMinor;

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
