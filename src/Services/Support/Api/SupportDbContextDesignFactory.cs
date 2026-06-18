using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ThreeCommerce.Support.Infrastructure;

namespace ThreeCommerce.Support.Api;

/// <summary>
/// Design-time factory: lets `dotnet ef` and the migration bundle build the DbContext
/// WITHOUT the application host (no InternalAuth / RabbitMQ / BL-11 secret guard). The DB
/// connection comes from `--connection` at apply time; the default matches local dev so
/// host-side `dotnet ef database update` keeps working unchanged.
/// </summary>
public sealed class SupportDbContextDesignFactory : IDesignTimeDbContextFactory<SupportDbContext>
{
    public SupportDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("MIGRATIONS_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=support_db;Username=support_svc;Password=support_dev";
        var options = new DbContextOptionsBuilder<SupportDbContext>().UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "public")).Options;
        return new SupportDbContext(options);
    }
}
