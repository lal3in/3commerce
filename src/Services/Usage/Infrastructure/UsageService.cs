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

        var chargeMinor = balance.UnbilledOverageChargeMinor;
        if (chargeMinor > 0)
        {
            await publisher.Publish(new UsageOverageCharge(
                tenantId, balance.CustomerEmail, balance.Meter, balance.UnbilledOverageQuantity, chargeMinor, balance.Currency,
                $"overage-{balance.Id}-{balance.OverageQuantity}"), ct);
            balance.MarkOverageBilled(clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
        }

        return balance;
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
