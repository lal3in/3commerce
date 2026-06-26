using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ThreeCommerce.Audit.Infrastructure;

namespace ThreeCommerce.Audit.Api;

public sealed class AuditDbContextDesignFactory : IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("MIGRATIONS_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=audit_db;Username=audit_svc;Password=audit_dev";
        return new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>()
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "public")).Options);
    }
}
