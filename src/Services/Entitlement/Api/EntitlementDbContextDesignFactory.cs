using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ThreeCommerce.Entitlement.Infrastructure;

namespace ThreeCommerce.Entitlement.Api;

public sealed class EntitlementDbContextDesignFactory : IDesignTimeDbContextFactory<EntitlementDbContext>
{
    public EntitlementDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("MIGRATIONS_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=entitlement_db;Username=entitlement_svc;Password=entitlement_dev";
        return new EntitlementDbContext(new DbContextOptionsBuilder<EntitlementDbContext>()
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "public")).Options);
    }
}
