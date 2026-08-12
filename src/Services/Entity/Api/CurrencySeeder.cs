using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Reference;
using ThreeCommerce.BuildingBlocks.Infrastructure.Tenancy;
using ThreeCommerce.Entity.Domain;
using ThreeCommerce.Entity.Infrastructure;

namespace ThreeCommerce.Entity.Api;

/// <summary>
/// Seeds the currency registry (currency_1) for the default tenant with the currencies the platform
/// already trades in, so the managed set isn't empty on first boot. Idempotent — only adds codes that are
/// missing, like <c>ChartOfAccountsSeeder</c>, so it can't clobber an operator's later edits.
/// </summary>
public static class CurrencySeeder
{
    // (Code, Name, Symbol, DecimalPlaces) — mostly 2-decimal, plus JPY as the 0-decimal example so a
    // 0-decimal currency flows registry → storefront → sale → dashboards out of the box (currency_4).
    // Operators can add further 0-decimal codes via the admin page (currency_1); display honours the
    // per-currency decimals (currency_3).
    private static readonly (string Code, string Name, string Symbol, int Decimals)[] Defaults =
    [
        ("AUD", "Australian Dollar", "A$", 2),
        ("CAD", "Canadian Dollar", "C$", 2),
        ("CNY", "Chinese Yuan", "¥", 2),
        ("EUR", "Euro", "€", 2),
        ("GBP", "British Pound", "£", 2),
        ("JPY", "Japanese Yen", "¥", 0),
        ("USD", "US Dollar", "$", 2),
    ];

    public static async Task SeedAsync(WebApplication app)
    {
        var tenantId = Guid.TryParse(app.Configuration["Tenancy:DefaultTenantId"], out var tid)
            ? tid
            : new Guid("00000000-0000-0000-0000-000000000001");

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EntityDbContext>();
        var publish = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
        try
        {
            if (!await db.Database.CanConnectAsync())
            {
                return;
            }

            // Currencies is tenant-isolated with FORCE RLS, so the reads + writes must run inside a tenant
            // scope or the policy hides existing rows (→ duplicate inserts) and blocks new ones. Seed as the
            // platform admin for the default tenant (transaction-local context, ADR-0024). Publish
            // CurrencyChanged for each new code (inside the tx so the bus outbox flushes on SaveChanges), so
            // consumers (Catalog's SupportedCurrency projection, currency_2) learn about the seeded set too.
            var added = await db.RunInTenantScopeAsync(
                new TenantContext(tenantId, null, IsPlatformAdmin: true),
                async () =>
                {
                    var existing = await db.Currencies.Where(c => c.TenantId == tenantId).ToDictionaryAsync(c => c.Code);
                    var now = DateTimeOffset.UtcNow;
                    var count = 0;
                    foreach (var (code, name, symbol, decimals) in Defaults)
                    {
                        if (!existing.ContainsKey(code))
                        {
                            db.Currencies.Add(Currency.Create(tenantId, code, name, symbol, decimals, now));
                            count++;
                        }

                        // Republish every default on each boot (the projection upsert is idempotent), so a
                        // consumer's SupportedCurrency read model is complete even for codes seeded before it
                        // existed (currency_2) — without this, enabled currencies seeded earlier would be
                        // absent from the projection and wrongly rejected on input.
                        var current = existing.GetValueOrDefault(code);
                        await publish.Publish(new CurrencyChanged(
                            tenantId, code, current?.Name ?? name, current?.Symbol ?? symbol,
                            current?.DecimalPlaces ?? decimals, current?.Enabled ?? true));
                    }

                    await db.SaveChangesAsync();
                    return count;
                });

            if (added > 0)
            {
                app.Logger.LogInformation("Seeded {Count} currencies", added);
            }
        }
        catch (Npgsql.PostgresException)
        {
            // schema not migrated yet (host start before Migrate) — skip
        }
    }
}
