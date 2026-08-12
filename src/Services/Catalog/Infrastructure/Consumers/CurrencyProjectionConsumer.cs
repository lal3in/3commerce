using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Reference;
using ThreeCommerce.Catalog.Domain;

namespace ThreeCommerce.Catalog.Infrastructure.Consumers;

/// <summary>
/// Projects the tenant currency registry (currency_2) onto Catalog's local <see cref="SupportedCurrency"/>
/// read model, fed by the Entity service's <see cref="CurrencyChanged"/>. Each event carries the current
/// truth, so the upsert is idempotent — mirrors <c>StorefrontCarrierReadinessConsumer</c>.
/// </summary>
public sealed class CurrencyProjectionConsumer(CatalogDbContext db) : IConsumer<CurrencyChanged>
{
    public async Task Consume(ConsumeContext<CurrencyChanged> context)
    {
        var m = context.Message;
        var row = await db.SupportedCurrencies.SingleOrDefaultAsync(
            c => c.TenantId == m.TenantId && c.Code == m.Code, context.CancellationToken);
        if (row is null)
        {
            row = new SupportedCurrency { TenantId = m.TenantId, Code = m.Code };
            db.SupportedCurrencies.Add(row);
        }

        row.Name = m.Name;
        row.Symbol = m.Symbol;
        row.DecimalPlaces = m.DecimalPlaces;
        row.Enabled = m.Enabled;
        row.UpdatedAt = context.SentTime ?? DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(context.CancellationToken);
    }
}
