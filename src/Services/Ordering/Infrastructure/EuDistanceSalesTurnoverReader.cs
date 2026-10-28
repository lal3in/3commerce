using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Infrastructure;

/// <summary>
/// Reads a seller's EU distance-sales running total (ADR-0063 P1b) — Σ of its <see cref="EuDistanceSale"/> rows in
/// EUR for the preceding and the current UTC calendar year. Ordering's tables have no row-level security; the tenant
/// filter is the seller filter (the tenant IS the seller of record, ADR-0063 D1).
/// </summary>
public static class EuDistanceSalesTurnoverReader
{
    public static async Task<EuDistanceSalesTurnover> EuDistanceSalesTurnoverAsync(
        this OrderingDbContext db, Guid tenantId, DateTimeOffset now, CancellationToken ct)
    {
        var year = now.UtcDateTime.Year;
        var totals = await db.EuDistanceSales.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.Currency == "EUR" && (s.Year == year || s.Year == year - 1))
            .GroupBy(s => s.Year)
            .Select(g => new { Year = g.Key, NetMinor = g.Sum(s => s.NetMinor) })
            .ToListAsync(ct);
        return new EuDistanceSalesTurnover(
            totals.Where(t => t.Year == year - 1).Sum(t => t.NetMinor),
            totals.Where(t => t.Year == year).Sum(t => t.NetMinor));
    }
}
