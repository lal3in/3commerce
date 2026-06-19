using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ThreeCommerce.Entity.Infrastructure;

namespace ThreeCommerce.Entity.Api;

/// <summary>
/// Design-time factory: lets `dotnet ef` and the migration bundle build the DbContext
/// WITHOUT the application host (no InternalAuth / RabbitMQ / BL-11 secret guard). The DB
/// connection comes from the migrator's connection at apply time; the default matches local
/// dev so host-side `dotnet ef database update` keeps working unchanged.
/// </summary>
public sealed class EntityDbContextDesignFactory : IDesignTimeDbContextFactory<EntityDbContext>
{
    public EntityDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("MIGRATIONS_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=entity_db;Username=entity_svc;Password=entity_dev";
        var options = new DbContextOptionsBuilder<EntityDbContext>()
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "public"))
            .Options;
        return new EntityDbContext(options);
    }
}
