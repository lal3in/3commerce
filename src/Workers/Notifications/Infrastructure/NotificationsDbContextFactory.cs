using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ThreeCommerce.Workers.Notifications.Infrastructure;

/// <summary>
/// Design-time factory so `dotnet ef migrations add` can build the context without running the app
/// (whose startup applies migrations + starts the bus). Uses the local-dev connection; migrations
/// carry no data, so the connection is only used for provider/model shape.
/// </summary>
public sealed class NotificationsDbContextFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=notifications_db;Username=notifications_svc;Password=notifications_dev",
                o => o.MigrationsHistoryTable("__EFMigrationsHistory", "public"))
            .Options;
        return new NotificationsDbContext(options);
    }
}
