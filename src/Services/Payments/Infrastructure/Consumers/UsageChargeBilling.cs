using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;
using ThreeCommerce.Payments.Infrastructure.Providers;

namespace ThreeCommerce.Payments.Infrastructure.Consumers;

/// <summary>A usage charge Usage asked for (<see cref="UsageOverageCharge"/> / <see cref="UsageAutoLoadCharge"/>).</summary>
public sealed record UsageChargeRequest(
    Guid TenantId, Guid? StorefrontId, string CustomerEmail, UsageChargeKind Kind, MeterType Meter, long Quantity, long UnitPriceMinor,
    long PriceMinor, string Currency, string Reference, string Description);

/// <summary>
/// Charge + posting + dunning of a metered usage charge — the arrears overage (<see cref="UsageOverageChargeConsumer"/>)
/// and the prepaid auto-load (<see cref="UsageAutoLoadChargeConsumer"/>) bill identically (pay_usage_charges_post_revenue):
/// <list type="number">
/// <item>de-dupe on <c>reference</c> — a posted reference is never charged again, and a charge Payments already answered
/// is never re-charged by a redelivered request (its retries are the schedule's);</item>
/// <item>resolve the tax from the balance storefront's CURRENT regime + rate (the unit price is a storefront shelf price,
/// ADR-0038) with checkout's arithmetic (<see cref="RecurringChargeTax"/>, ADR-0063 P4), charging the amount itself on an
/// inclusive / 0% / unknown-rate store and the amount + the added tax on an exclusive (US) store (owner decision
/// 2026-10-15, option A) — PINNED to the reference on the first attempt (<see cref="RecurringChargePin"/>), so every
/// attempt sends the same amount;</item>
/// <item>on a settled charge, post a receivable-bridged <see cref="Ledger.Sale"/> attributed to the storefront (net to
/// <c>revenue.store-{id}</c>, tax to <c>tax.store-{id}</c>, gross to <c>cash.store-{id}.{provider}</c>) and publish
/// <see cref="UsageChargeSettled"/> with its tax facts (ADR-0063 P4b) in the SAME unit of work;</item>
/// <item>on an unsettled one (Failed / RequiresAction / Cancelled) post nothing, dun it like a renewal
/// (<see cref="DunningSchedule"/>: days 1, 3 and 7, a fresh provider key per answered attempt, the same pinned amount, then
/// abandoned) and publish <see cref="UsageChargeFailed"/> — Usage reverses the optimistic credit / marks the overage unpaid,
/// Notifications emails the customer (owner decision 2026-10-27, <c>usage_charge_failure_feedback</c>).</item>
/// </list>
/// A usage charge has no ship-to, so it is never destination-exempt. <paramref name="publisher"/> is the scoped bus-outbox
/// endpoint: what it publishes commits with the outcome's SaveChanges, or not at all.
/// </summary>
public sealed class UsageChargeBilling(
    PaymentsDbContext db, IPaymentProviderRegistry registry, PaymentModeResolver modeResolver, TimeProvider time,
    ILogger<UsageChargeBilling> logger, IPublishEndpoint publisher)
{
    /// <summary>
    /// Usage's request: record the charge (once per reference) and make its first attempt — or, on a redelivery, nothing
    /// unless the last attempt was never answered by the provider (then it is retried under the SAME key, which the
    /// provider de-duplicates).
    /// </summary>
    public async Task ChargeAsync(UsageChargeRequest request, CancellationToken ct)
    {
        if (await db.JournalEntries.AnyAsync(e => e.Reference == request.Reference, ct))
        {
            return; // already charged + posted (also a charge settled before charges were recorded)
        }

        var charge = await db.UsageCharges.SingleOrDefaultAsync(c => c.Reference == request.Reference, ct);
        if (charge is null)
        {
            charge = UsageCharge.Record(
                request.TenantId, request.StorefrontId, request.CustomerEmail, request.Kind, request.Meter, request.Quantity,
                request.UnitPriceMinor, request.PriceMinor, request.Currency, request.Reference, request.Description, time.GetUtcNow());
            db.UsageCharges.Add(charge);
            await db.SaveChangesAsync(ct);
        }
        else if (!(charge.Status == UsageChargeStatus.Pending
            || (charge.Status == UsageChargeStatus.PastDue && charge.LastFailureReason == SubscriptionChargeOutcomes.Error)))
        {
            logger.LogInformation(
                "Usage charge {Reference} re-requested while {Status}; nothing charged (retries follow the dunning schedule)",
                charge.Reference, charge.Status);
            return;
        }

        await AttemptAsync(charge, UsageChargeTrigger.Initial, finalRetry: false, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The scheduled dunning retries (owner decision 2026-10-27): every past-due usage charge whose retry (days 1, 3 and 7
    /// after its first failure) has come is charged again under a fresh key with its pinned amount — each one committed on
    /// its own before the next is touched; the final one not settling abandons it. Returns the number retried.
    /// </summary>
    public async Task<int> RetryDueAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var firstRetry = now.AddDays(-DunningSchedule.RetryDays[0]);
        var pastDue = await db.UsageCharges
            .Where(c => c.Status == UsageChargeStatus.PastDue && c.DunningStartedAt != null && c.DunningStartedAt <= firstRetry)
            .OrderBy(c => c.DunningStartedAt)
            .ToListAsync(ct);

        var retried = 0;
        foreach (var charge in pastDue)
        {
            if (charge.ScheduledRetryDue(now) is { } retry)
            {
                await AttemptAsync(charge, UsageChargeTrigger.ScheduledRetry, retry.IsFinal, ct);
                await db.SaveChangesAsync(ct);
                retried++;
            }
        }

        return retried;
    }

    /// <summary>
    /// A provider event says a usage attempt's PaymentIntent succeeded later (e.g. the customer authenticated): settle the
    /// charge exactly once — posted and announced — even if it had been abandoned (the usage was consumed; the money is
    /// owed). Money for a charge that is already settled by another attempt is not booked twice: it is logged as an error
    /// naming the intent for a manual refund. Returns false when the intent is not a usage attempt.
    /// </summary>
    public async Task<bool> SettleAttemptAsync(string paymentIntentId, CancellationToken ct)
    {
        var attempt = await db.UsageChargeAttempts.SingleOrDefaultAsync(a => a.PaymentIntentId == paymentIntentId, ct);
        if (attempt is null)
        {
            return false;
        }

        if (attempt.SettledAt is not null)
        {
            return true; // already settled (the immediate answer, or an earlier delivery of the event)
        }

        var charge = await db.UsageCharges.SingleAsync(c => c.Id == attempt.UsageChargeId, ct);
        attempt.Outcome = nameof(PaymentOutcome.Succeeded);
        if (charge.Status == UsageChargeStatus.Settled)
        {
            logger.LogError(
                "Usage charge attempt {Key} succeeded at the provider but charge {Reference} was already settled; refund intent {Intent} manually",
                attempt.IdempotencyKey, charge.Reference, paymentIntentId);
            return true;
        }

        var tax = await RecurringChargePin.PinAsync(db, time, charge.Reference, () => ComputeTax(charge, null), ct);
        await SettleAsync(charge, attempt, tax, new PaymentResponse(paymentIntentId, null, PaymentOutcome.Succeeded), ct);
        return true;
    }

    /// <summary>
    /// One attempt: pin → attempt row (committed before the provider call) → the provider → settle or dun. An attempt the
    /// provider never answered (it threw) duns silently (reason <c>Error</c>, no event — no customer-visible failure) and
    /// keeps its key for the next try. Recording a SETTLED charge happens outside the catch, so a failure to record money
    /// that moved aborts (and is retried under the same key) instead of dunning a customer who paid.
    /// </summary>
    private async Task AttemptAsync(UsageCharge charge, UsageChargeTrigger trigger, bool finalRetry, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        RecurringChargeTaxResult tax;
        UsageChargeAttempt attempt;
        PaymentResponse response;
        try
        {
            var accounts = await AccountsAsync(charge.StorefrontId, ct);
            // Computed once per reference and PINNED (persisted before the provider call): every attempt — a retry after a
            // failure, a redelivery, a rate change in between — charges the same amount.
            tax = await RecurringChargePin.PinAsync(db, time, charge.Reference, () => ComputeTax(charge, accounts), ct);
            attempt = await BeginAttemptAsync(charge, trigger, tax, now, ct);

            var account = modeResolver.DefaultAccountForHost();
            response = await registry.Resolve(account).AuthorizeAsync(
                new PaymentRequest(Guid.Empty, tax.ChargeMinor, charge.Currency, attempt.IdempotencyKey, PaymentMethodKind.Card, account), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Usage charge {Reference} could not be charged; marked past-due and retried on schedule", charge.Reference);
            charge.MarkPastDue(now, SubscriptionChargeOutcomes.Error);
            return;
        }

        attempt.PaymentIntentId = response.PaymentIntentId;
        attempt.Outcome = response.Outcome.ToString();
        charge.AttemptAnswered(now);
        if (response.Outcome != PaymentOutcome.Succeeded)
        {
            await DunAsync(charge, attempt, response, tax, finalRetry, now, ct);
            return;
        }

        await SettleAsync(charge, attempt, tax, response, ct);
    }

    /// <summary>The attempt this charge is made under, COMMITTED before the provider is asked: the last one again when the
    /// provider never answered it (same key), else a new one with a fresh key.</summary>
    private async Task<UsageChargeAttempt> BeginAttemptAsync(
        UsageCharge charge, UsageChargeTrigger trigger, RecurringChargeTaxResult tax, DateTimeOffset now, CancellationToken ct)
    {
        var last = await db.UsageChargeAttempts
            .Where(a => a.UsageChargeId == charge.Id)
            .OrderByDescending(a => a.Attempt)
            .FirstOrDefaultAsync(ct);
        if (last is { Outcome: SubscriptionChargeOutcomes.Pending })
        {
            return last;
        }

        var attempt = new UsageChargeAttempt
        {
            Id = Guid.CreateVersion7(),
            TenantId = charge.TenantId,
            UsageChargeId = charge.Id,
            Attempt = charge.AttemptCount,
            IdempotencyKey = charge.NextAttemptKey,
            Trigger = trigger,
            AmountMinor = tax.ChargeMinor,
            Currency = charge.Currency,
            Outcome = SubscriptionChargeOutcomes.Pending,
            CreatedAt = StoredTime.Of(now),
        };
        charge.KeyIssued(now);
        db.UsageChargeAttempts.Add(attempt);
        await db.SaveChangesAsync(ct);
        return attempt;
    }

    /// <summary>
    /// The provider answered without settling: past due (the first failure starts the schedule), nothing posted, and Usage
    /// + the customer are told (<see cref="UsageChargeFailed"/>, bus outbox). The final scheduled retry not settling
    /// abandons the charge.
    /// </summary>
    private async Task DunAsync(
        UsageCharge charge, UsageChargeAttempt attempt, PaymentResponse response, RecurringChargeTaxResult tax, bool finalRetry,
        DateTimeOffset now, CancellationToken ct)
    {
        charge.MarkPastDue(now, response.Outcome.ToString());
        if (finalRetry)
        {
            charge.Abandon(now);
        }

        logger.LogWarning(
            "Usage charge {Reference} for storefront {StorefrontId} was not settled ({Outcome}, {ErrorCode}, attempt {Attempt}); nothing posted, {State}",
            charge.Reference, charge.StorefrontId, response.Outcome, response.Error?.Code, attempt.Attempt,
            finalRetry ? "abandoned after its final retry" : $"retried at {charge.NextRetryAt:O}");

        await publisher.Publish(new UsageChargeFailed(
            charge.TenantId, charge.StorefrontId, charge.CustomerEmail, charge.Kind, charge.Meter, charge.Quantity, charge.Reference,
            tax.ChargeMinor, charge.Currency, FailureReason(response.Outcome), attempt.Attempt, StoredTime.Of(now),
            charge.NextRetryAt, charge.AbandonsAfter, finalRetry), ct);
    }

    private static ChargeFailureReason FailureReason(PaymentOutcome outcome) => outcome switch
    {
        PaymentOutcome.RequiresAction => ChargeFailureReason.RequiresAction,
        PaymentOutcome.Cancelled => ChargeFailureReason.Cancelled,
        _ => ChargeFailureReason.Failed,
    };

    private RecurringChargeTaxResult ComputeTax(UsageCharge charge, StorefrontLedgerAccounts? accounts)
    {
        var taxInclusive = accounts?.TaxInclusive ?? false;
        var computed = RecurringChargeTax.For(charge.PriceMinor, accounts?.TaxRateBasisPoints, taxInclusive);
        SubscriptionService.LogUnknownRate(logger, computed, taxInclusive, charge.Reference, charge.StorefrontId);
        return computed;
    }

    private async Task<StorefrontLedgerAccounts?> AccountsAsync(Guid? storefrontId, CancellationToken ct) =>
        storefrontId is { } sid
            ? await db.StorefrontLedgerAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.StorefrontId == sid, ct)
            : null;

    /// <summary>
    /// The charge settled: mark it, announce it (<see cref="UsageChargeSettled"/> with the P1-shaped tax facts of exactly
    /// the split posted, ADR-0063 P4b) and post the sale — one SaveChanges, so the event exists iff the entry does. A
    /// reference that is somehow posted already is marked settled without a second entry or event.
    /// </summary>
    private async Task SettleAsync(
        UsageCharge charge, UsageChargeAttempt attempt, RecurringChargeTaxResult tax, PaymentResponse response, CancellationToken ct)
    {
        var storefrontId = charge.StorefrontId;
        var currency = charge.Currency;
        var reference = charge.Reference;
        var description = charge.Description;
        var accounts = await AccountsAsync(storefrontId, ct);
        var account = modeResolver.DefaultAccountForHost();
        var now = time.GetUtcNow();
        charge.Settle(now);
        attempt.SettledAt = StoredTime.Of(now);
        if (await db.JournalEntries.AnyAsync(e => e.Reference == reference, ct))
        {
            return; // already posted (and announced) for this reference
        }

        var facts = SettledChargeTax.For(tax, description, charge.Quantity, exempt: false, accounts?.TaxInclusive ?? false, accounts?.TaxRateBasisPoints);
        await publisher.Publish(new UsageChargeSettled(
            charge.TenantId, storefrontId, charge.CustomerEmail, charge.Kind, charge.Meter, charge.Quantity, charge.UnitPriceMinor,
            reference, currency, facts.NetMinor, facts.TaxMinor, facts.GrossMinor, facts.TaxInclusive, facts.Scheme, [facts.Line],
            StoredTime.Of(now), attempt.Attempt, response.PaymentIntentId), ct);

        var sale = Ledger.Sale(
            Guid.Empty, tax.ChargeMinor, tax.TaxMinor, 0, currency, time.GetUtcNow(),
            PaymentMethodKind.Card, account.Provider, accounts?.RevenueAccountCode, accounts?.TaxAccountCode, accounts?.ReceivableAccountCode,
            0, accounts?.ShippingAccountCode, storefrontId, reference, description);
        if (sale.Lines.Count > 0)
        {
            db.JournalEntries.Add(sale);
            // renewal_usage_refund_path: the settled usage charge is a Payment of its own (kind UsageCharge, no order,
            // keyed by this reference, carrying its intent id and the tax this entry booked), so the refund path, partial
            // refunds and the dispute webhooks reach it. Committed with the entry, or not at all.
            db.Payments.Add(Payment.ForSettledCharge(
                PaymentKind.UsageCharge, sale, accounts?.TaxAccountCode, Guid.Empty, null, response.PaymentIntentId,
                tax.ChargeMinor, currency, account.Provider, storefrontId, null, null, time.GetUtcNow()));
            await db.SaveChangesAsync(ct);
        }
    }
}
