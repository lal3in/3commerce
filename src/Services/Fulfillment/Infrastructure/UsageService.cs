using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Fulfillment.Domain;

namespace ThreeCommerce.Fulfillment.Infrastructure;

/// <summary>
/// Metered usage (mt7_4): provision an allowance, record append-only usage events, and keep the
/// balance's used quantity incrementally (never re-summing records on read). One current balance per
/// (tenant, customer, meter). Overage/billing is mt7_5.
/// </summary>
public sealed class UsageService(FulfillmentDbContext db, TimeProvider clock)
{
    public async Task<UsageBalance> ProvisionAsync(
        Guid tenantId, string email, MeterType meter, long includedQuantity, DateTimeOffset? periodEnd, CancellationToken ct)
    {
        var balance = await CurrentBalanceAsync(tenantId, email, meter, create: true, ct);
        balance!.Provision(includedQuantity, periodEnd, clock.GetUtcNow());
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
