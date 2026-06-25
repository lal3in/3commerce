using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ThreeCommerce.Pricing.Infrastructure;

namespace ThreeCommerce.Pricing.Api;

public sealed class PricingDbContextDesignFactory : IDesignTimeDbContextFactory<PricingDbContext>
{
    public PricingDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("MIGRATIONS_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=pricing_db;Username=pricing_svc;Password=pricing_dev";
        return new PricingDbContext(new DbContextOptionsBuilder<PricingDbContext>()
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "public")).Options);
    }
}
