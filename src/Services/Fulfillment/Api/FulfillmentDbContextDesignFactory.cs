using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ThreeCommerce.Fulfillment.Infrastructure;

namespace ThreeCommerce.Fulfillment.Api;

/// <summary>
/// Design-time factory: lets `dotnet ef` and the migration bundle build the DbContext
/// WITHOUT the application host (no InternalAuth / RabbitMQ / BL-11 secret guard). The DB
/// connection comes from `--connection` at apply time; the default matches local dev so
/// host-side `dotnet ef database update` keeps working unchanged.
/// </summary>
public sealed class FulfillmentDbContextDesignFactory : IDesignTimeDbContextFactory<FulfillmentDbContext>
{
    public FulfillmentDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("MIGRATIONS_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=fulfillment_db;Username=fulfillment_svc;Password=fulfillment_dev";
        var options = new DbContextOptionsBuilder<FulfillmentDbContext>().UseNpgsql(connectionString).Options;
        return new FulfillmentDbContext(options);
    }
}
