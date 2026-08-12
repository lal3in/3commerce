using Microsoft.EntityFrameworkCore;
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
    // (Code, Name, Symbol, DecimalPlaces) — all 2-decimal today; JPY-style 0-decimal codes are added by
    // operators via the admin page (currency_1) and honoured in display once currency_3 lands.
    private static readonly (string Code, string Name, string Symbol, int Decimals)[] Defaults =
    [
        ("AUD", "Australian Dollar", "A$", 2),
        ("CAD", "Canadian Dollar", "C$", 2),
        ("CNY", "Chinese Yuan", "¥", 2),
        ("EUR", "Euro", "€", 2),
        ("GBP", "British Pound", "£", 2),
        ("USD", "US Dollar", "$", 2),
    ];

    public static async Task SeedAsync(WebApplication app)
    {
        var tenantId = Guid.TryParse(app.Configuration["Tenancy:DefaultTenantId"], out var tid)
            ? tid
            : new Guid("00000000-0000-0000-0000-000000000001");

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EntityDbContext>();
        try
        {
            if (!await db.Database.CanConnectAsync())
            {
                return;
            }

            // Currencies is tenant-isolated with FORCE RLS, so the reads + writes must run inside a tenant
            // scope or the policy hides existing rows (→ duplicate inserts) and blocks new ones. Seed as the
            // platform admin for the default tenant (transaction-local context, ADR-0024).
            var added = await db.RunInTenantScopeAsync(
                new TenantContext(tenantId, null, IsPlatformAdmin: true),
                async () =>
                {
                    var existing = await db.Currencies.Where(c => c.TenantId == tenantId).Select(c => c.Code).ToListAsync();
                    var now = DateTimeOffset.UtcNow;
                    var count = 0;
                    foreach (var (code, name, symbol, decimals) in Defaults)
                    {
                        if (existing.Contains(code))
                        {
                            continue;
                        }

                        db.Currencies.Add(Currency.Create(tenantId, code, name, symbol, decimals, now));
                        count++;
                    }

                    if (count > 0)
                    {
                        await db.SaveChangesAsync();
                    }

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
