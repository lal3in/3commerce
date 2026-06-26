using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ThreeCommerce.Marketing.Infrastructure;

namespace ThreeCommerce.Marketing.Api;

/// <summary>Design-time factory so `dotnet ef` / the migration bundle build the context without the host.</summary>
public sealed class MarketingDbContextDesignFactory : IDesignTimeDbContextFactory<MarketingDbContext>
{
    public MarketingDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("MIGRATIONS_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=marketing_db;Username=marketing_svc;Password=marketing_dev";
        var options = new DbContextOptionsBuilder<MarketingDbContext>()
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "public"))
            .Options;
        return new MarketingDbContext(options);
    }
}
