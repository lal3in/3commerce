using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Usage.Domain;

namespace ThreeCommerce.Usage.Infrastructure;

/// <summary>
/// Metered usage (mt7_4/mt7_5): provision an allowance + overage price, record append-only usage with
/// the balance kept incrementally (O(1) reads), gate access when overage is not allowed, and bill the
/// unbilled overage by charging it via the rail (UsageOverageCharge → Payments). Every overage / auto-load charge it
/// publishes is recorded (<see cref="UsageCharge"/>) and moved by Payments' outcome — settled, or declined and retried,
/// or abandoned — which reverses the optimistic auto-load credit and holds the balance's billing on failure
/// (usage_charge_failure_feedback, owner decision 2026-10-27).
/// </summary>
public sealed class UsageService(UsageDbContext db, IPublishEndpoint publisher, TimeProvider clock)
{
    public async Task<UsageBalance> ProvisionAsync(
        Guid tenantId, string email, MeterType meter, long includedQuantity,
        bool overageAllowed, long overageUnitPriceMinor, string currency, DateTimeOffset? periodEnd, CancellationToken ct,
        Guid? storefrontId = null)
    {
        var balance = (await CurrentBalanceAsync(tenantId, email, meter, create: true, ct))!;
        balance.Provision(includedQuantity, overageAllowed, overageUnitPriceMinor, currency, periodEnd, clock.GetUtcNow(), storefrontId);
        await db.SaveChangesAsync(ct);
        return balance;
    }

    public async Task<UsageBalance> RecordAsync(
        Guid tenantId, string email, MeterType meter, long quantity, string? reference, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(reference)
            && await db.UsageRecords.AnyAsync(r => r.TenantId == tenantId && r.ReferenceId == reference, ct))
        {
            return (await CurrentBalanceAsync(tenantId, email, meter, create: false, ct))
                ?? UsageBalance.Create(tenantId, email, meter, clock.GetUtcNow());
        }

        var now = clock.GetUtcNow();
        var balance = (await CurrentBalanceAsync(tenantId, email, meter, create: true, ct))!;
        if (!balance.CanAccept(quantity))
        {
            throw new UsageRuleException(balance.PaymentHold == UsagePaymentHold.None
                ? "Usage allowance exhausted and overage is not permitted for this plan."
                : "Usage allowance exhausted and further usage is on hold until an unpaid usage charge is paid.");
        }

        balance.Add(quantity, now);
        db.UsageRecords.Add(new UsageRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            BalanceId = balance.Id,
            CustomerEmail = balance.CustomerEmail,
            Meter = meter,
            Quantity = quantity,
            ReferenceId = reference,
            OccurredAt = now,
        });

        // Prepaid auto-load (jobmgr_3): usage draws down the prepaid credit and, once it reaches the
        // customer's threshold, tops up + charges off-session the moment the threshold is crossed.
        if (balance.AutoLoadEnabled)
        {
            balance.ConsumePrepaid(quantity, now);
            if (balance.ShouldAutoLoad())
            {
                await PublishAutoLoadAsync(balance, ct);
            }
        }

        await db.SaveChangesAsync(ct);
        return balance;
    }

    /// <summary>Set a customer's prepaid auto-load preferences (they choose the threshold + reload amount).</summary>
    public async Task<UsageBalance> ConfigureAutoLoadAsync(
        Guid tenantId, string email, MeterType meter, bool enabled, long thresholdQuantity, long reloadQuantity, CancellationToken ct)
    {
        var balance = (await CurrentBalanceAsync(tenantId, email, meter, create: true, ct))!;
        balance.ConfigureAutoLoad(enabled, thresholdQuantity, reloadQuantity, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return balance;
    }

    /// <summary>
    /// Daily safety net (jobmgr_3): top up every auto-load balance sitting at/below its threshold — catches
    /// a top-up missed by the event path (e.g. the threshold was lowered, or auto-load was enabled while
    /// already low). Idempotent per top-up (the charge reference carries the auto-load count).
    /// </summary>
    public async Task<int> SweepAutoLoadsAsync(CancellationToken ct)
    {
        var due = await db.UsageBalances
            .Where(b => b.AutoLoadEnabled && b.AutoLoadReloadQuantity > 0 && b.PrepaidRemainingQuantity <= b.AutoLoadThresholdQuantity)
            .ToListAsync(ct);
        foreach (var balance in due)
        {
            await PublishAutoLoadAsync(balance, ct);
        }

        if (due.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return due.Count;
    }

    /// <summary>Credit one reload and publish the off-session charge (unless the reload is free). The caller
    /// owns SaveChanges so a sweep commits all top-ups together.</summary>
    private async Task PublishAutoLoadAsync(UsageBalance balance, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var chargeMinor = balance.ApplyAutoLoad(now);
        if (chargeMinor <= 0)
        {
            return; // free reload (no unit price) — credit only, nothing to bill
        }

        var reference = balance.AutoLoadChargeReference;
        db.UsageCharges.Add(UsageCharge.Open(
            balance.TenantId, balance.Id, reference, UsageChargeKind.AutoLoad, balance.AutoLoadReloadQuantity, chargeMinor, balance.Currency, now));
        await publisher.Publish(new UsageAutoLoadCharge(
            balance.TenantId, balance.CustomerEmail, balance.Meter, balance.AutoLoadReloadQuantity, chargeMinor, balance.Currency,
            reference, balance.StorefrontId, balance.OverageUnitPriceMinor), ct);
    }

    /// <summary>Charge the unbilled overage via the rail (mt7_5). No-op when there is nothing to bill.</summary>
    public async Task<UsageBalance?> BillOverageAsync(Guid tenantId, Guid balanceId, CancellationToken ct)
    {
        var balance = await db.UsageBalances.SingleOrDefaultAsync(b => b.Id == balanceId && b.TenantId == tenantId, ct);
        if (balance is null)
        {
            return null;
        }

        if (await BillUnbilledOverageAsync(balance, ct))
        {
            await db.SaveChangesAsync(ct);
        }

        return balance;
    }

    /// <summary>
    /// Close every billing period whose window has ended (mt7_5 closing flow): bill any unbilled overage via
    /// the rail, then roll the balance to the next period (counters reset, window advances). Idempotent — a
    /// balance re-swept before new usage has nothing left to bill and simply keeps its rolled window.
    /// Returns how many balances were closed. Auto-run on a cron; also callable by an operator.
    /// </summary>
    public async Task<int> CloseDuePeriodsAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var due = await db.UsageBalances
            .Where(b => b.PeriodEnd != null && b.PeriodEnd <= now)
            .ToListAsync(ct);

        foreach (var balance in due)
        {
            await BillUnbilledOverageAsync(balance, ct); // bill BEFORE rolling, or the overage is lost
            balance.RollToNextPeriod(now);
        }

        if (due.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return due.Count;
    }

    /// <summary>Publishes the unbilled overage charge and marks it billed. Returns whether anything was billed.
    /// The caller owns the SaveChanges so a sweep commits all rolled balances in one transaction.</summary>
    private async Task<bool> BillUnbilledOverageAsync(UsageBalance balance, CancellationToken ct)
    {
        var chargeMinor = balance.UnbilledOverageChargeMinor;
        if (chargeMinor <= 0)
        {
            return false;
        }

        var now = clock.GetUtcNow();
        var reference = balance.OverageChargeReference;
        db.UsageCharges.Add(UsageCharge.Open(
            balance.TenantId, balance.Id, reference, UsageChargeKind.Overage, balance.UnbilledOverageQuantity, chargeMinor, balance.Currency, now));
        await publisher.Publish(new UsageOverageCharge(
            balance.TenantId, balance.CustomerEmail, balance.Meter, balance.UnbilledOverageQuantity, chargeMinor, balance.Currency,
            reference, balance.StorefrontId, balance.OverageUnitPriceMinor), ct);
        balance.MarkOverageBilled(now);
        return true;
    }

    /// <summary>
    /// Payments settled a charge (<see cref="UsageChargeSettled"/>): mark it paid; if it had been declined, re-grant the
    /// auto-load credit taken back, and release the balance's hold when nothing else is unpaid. Idempotent per reference.
    /// </summary>
    public async Task ApplySettledAsync(UsageChargeSettled m, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var shelfMinor = m.UnitPriceMinor > 0 ? m.UnitPriceMinor * m.Quantity : m.GrossMinor;
        var (charge, balance) = await ChargeForAsync(m.TenantId, m.CustomerEmail, m.Meter, m.Reference, m.Kind, m.Quantity, shelfMinor, m.Currency, ct);
        if (charge is null || balance is null)
        {
            return;
        }

        if (charge.Settle(now) && charge.Kind == UsageChargeKind.AutoLoad)
        {
            balance.RestoreAutoLoad(charge.Quantity, now);
        }

        await RefreshHoldAsync(balance, charge, now, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Payments could not settle a charge (<see cref="UsageChargeFailed"/>): the first failure takes back the auto-load's
    /// optimistic credit (an overage simply stays billed-but-unpaid — it is never billed twice); the balance's billing is
    /// held (Unpaid while Payments retries, Suspended once the final retry failed). Idempotent per reference.
    /// </summary>
    public async Task ApplyFailedAsync(UsageChargeFailed m, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var (charge, balance) = await ChargeForAsync(m.TenantId, m.CustomerEmail, m.Meter, m.Reference, m.Kind, m.Quantity, m.AmountMinor, m.Currency, ct);
        if (charge is null || balance is null)
        {
            return;
        }

        if (charge.Fail(m.Reason, m.NextRetryAt, m.Final, now) && charge.Kind == UsageChargeKind.AutoLoad)
        {
            balance.ReverseAutoLoad(charge.Quantity, now);
        }

        await RefreshHoldAsync(balance, charge, now, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The recorded charge and its balance. A charge published before charges were recorded is recorded now
    /// (Open — its credit / billed mark was applied at publish, as it is today). Null when the balance is unknown.</summary>
    private async Task<(UsageCharge? Charge, UsageBalance? Balance)> ChargeForAsync(
        Guid tenantId, string email, MeterType meter, string reference, UsageChargeKind kind, long quantity, long amountMinor,
        string currency, CancellationToken ct)
    {
        var charge = await db.UsageCharges.SingleOrDefaultAsync(c => c.TenantId == tenantId && c.Reference == reference, ct);
        var balance = charge is not null
            ? await db.UsageBalances.SingleOrDefaultAsync(b => b.Id == charge.BalanceId, ct)
            : await CurrentBalanceAsync(tenantId, email, meter, create: false, ct);
        if (balance is null)
        {
            return (null, null);
        }

        if (charge is null)
        {
            charge = UsageCharge.Open(tenantId, balance.Id, reference, kind, quantity, amountMinor, currency, clock.GetUtcNow());
            db.UsageCharges.Add(charge);
        }

        return (charge, balance);
    }

    /// <summary>Derive the balance's hold from all its charges (<paramref name="changed"/> as it is now, the others as stored).</summary>
    private async Task RefreshHoldAsync(UsageBalance balance, UsageCharge changed, DateTimeOffset now, CancellationToken ct)
    {
        var others = await db.UsageCharges.AsNoTracking()
            .Where(c => c.BalanceId == balance.Id && c.Id != changed.Id
                && (c.State == UsageChargeState.Unpaid || c.State == UsageChargeState.Abandoned))
            .Select(c => c.State)
            .ToListAsync(ct);
        balance.SetPaymentHold(UsageCharge.HoldFor(others.Append(changed.State)), now);
    }

    public Task<List<UsageBalance>> ListBalancesAsync(Guid tenantId, string? email, CancellationToken ct)
    {
        var query = db.UsageBalances.AsNoTracking().Where(b => b.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(email))
        {
            var normalized = email.Trim().ToLowerInvariant();
            query = query.Where(b => b.CustomerEmail == normalized);
        }

        return query.OrderBy(b => b.Meter).ToListAsync(ct);
    }

    private async Task<UsageBalance?> CurrentBalanceAsync(Guid tenantId, string email, MeterType meter, bool create, CancellationToken ct)
    {
        var normalized = email.Trim().ToLowerInvariant();
        var balance = await db.UsageBalances.SingleOrDefaultAsync(
            b => b.TenantId == tenantId && b.CustomerEmail == normalized && b.Meter == meter, ct);
        if (balance is null && create)
        {
            balance = UsageBalance.Create(tenantId, email, meter, clock.GetUtcNow());
            db.UsageBalances.Add(balance);
        }

        return balance;
    }
}
