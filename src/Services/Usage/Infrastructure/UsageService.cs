using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Usage.Domain;

namespace ThreeCommerce.Usage.Infrastructure;

/// <summary>
/// Metered usage (mt7_4/mt7_5): provision an allowance + overage price, record append-only usage with
/// the balance kept incrementally (O(1) reads), gate access when overage is not allowed, and bill the
/// unbilled overage by charging it via the rail (UsageOverageCharge → Payments).
/// </summary>
public sealed class UsageService(UsageDbContext db, IPublishEndpoint publisher, TimeProvider clock)
{
    public async Task<UsageBalance> ProvisionAsync(
        Guid tenantId, string email, MeterType meter, long includedQuantity,
        bool overageAllowed, long overageUnitPriceMinor, string currency, DateTimeOffset? periodEnd, CancellationToken ct)
    {
        var balance = (await CurrentBalanceAsync(tenantId, email, meter, create: true, ct))!;
        balance.Provision(includedQuantity, overageAllowed, overageUnitPriceMinor, currency, periodEnd, clock.GetUtcNow());
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
            throw new UsageRuleException("Usage allowance exhausted and overage is not permitted for this plan.");
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
        await db.SaveChangesAsync(ct);
        return balance;
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

        await publisher.Publish(new UsageOverageCharge(
            balance.TenantId, balance.CustomerEmail, balance.Meter, balance.UnbilledOverageQuantity, chargeMinor, balance.Currency,
            $"overage-{balance.Id}-{balance.OverageQuantity}"), ct);
        balance.MarkOverageBilled(clock.GetUtcNow());
        return true;
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
