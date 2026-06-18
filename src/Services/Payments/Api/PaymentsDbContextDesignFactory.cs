using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.Payments.Api;

/// <summary>
/// Design-time factory: lets `dotnet ef` and the migration bundle build the DbContext
/// WITHOUT the application host (no InternalAuth / RabbitMQ / BL-11 secret guard). The DB
/// connection comes from `--connection` at apply time; the default matches local dev so
/// host-side `dotnet ef database update` keeps working unchanged.
/// </summary>
public sealed class PaymentsDbContextDesignFactory : IDesignTimeDbContextFactory<PaymentsDbContext>
{
    public PaymentsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("MIGRATIONS_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=payments_db;Username=payments_svc;Password=payments_dev";
        var options = new DbContextOptionsBuilder<PaymentsDbContext>().UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "public")).Options;
        return new PaymentsDbContext(options);
    }
}
