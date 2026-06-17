using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ThreeCommerce.Catalog.Infrastructure;

namespace ThreeCommerce.Catalog.Api;

/// <summary>
/// Design-time factory: lets `dotnet ef` and the migration bundle build the DbContext
/// WITHOUT the application host (no InternalAuth / RabbitMQ / BL-11 secret guard). The DB
/// connection comes from `--connection` at apply time; the default matches local dev so
/// host-side `dotnet ef database update` keeps working unchanged.
/// </summary>
public sealed class CatalogDbContextDesignFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("MIGRATIONS_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=catalog_db;Username=catalog_svc;Password=catalog_dev";
        var options = new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(connectionString).Options;
        return new CatalogDbContext(options);
    }
}
