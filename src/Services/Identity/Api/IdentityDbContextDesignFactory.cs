using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ThreeCommerce.Identity.Infrastructure;

namespace ThreeCommerce.Identity.Api;

/// <summary>
/// Design-time factory: lets `dotnet ef` and the migration bundle build the DbContext
/// WITHOUT the application host (no InternalAuth / RabbitMQ / BL-11 secret guard). The DB
/// connection comes from `--connection` at apply time; the default matches local dev so
/// host-side `dotnet ef database update` keeps working unchanged.
/// </summary>
public sealed class IdentityDbContextDesignFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("MIGRATIONS_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=identity_db;Username=identity_svc;Password=identity_dev";
        var options = new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "public")).Options;
        return new IdentityDbContext(options);
    }
}
