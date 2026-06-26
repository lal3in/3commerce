using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ThreeCommerce.Usage.Infrastructure;

namespace ThreeCommerce.Usage.Api;

public sealed class UsageDbContextDesignFactory : IDesignTimeDbContextFactory<UsageDbContext>
{
    public UsageDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("MIGRATIONS_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=usage_db;Username=usage_svc;Password=usage_dev";
        return new UsageDbContext(new DbContextOptionsBuilder<UsageDbContext>()
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "public")).Options);
    }
}
