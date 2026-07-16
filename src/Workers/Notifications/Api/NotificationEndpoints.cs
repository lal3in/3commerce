using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Workers.Notifications.Domain;
using ThreeCommerce.Workers.Notifications.Infrastructure;

namespace ThreeCommerce.Workers.Notifications.Api;

public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotifications(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/notifications").WithTags("Notifications")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);
        group.MapGet("/", GetSummary).WithSummary("Notification delivery counts (total, sent 24h, failed).");
        group.MapGet("/recent", GetRecent).WithSummary("Most recent notification deliveries (recipient masked).");
        return app;
    }

    private static async Task<Ok<NotificationSummaryDto>> GetSummary(
        NotificationsDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var since = clock.GetUtcNow().AddDays(-1);
        var total = await db.Deliveries.CountAsync(ct);
        var sent24 = await db.Deliveries.CountAsync(d => d.Status == NotificationStatus.Sent && d.OccurredAt >= since, ct);
        var failed = await db.Deliveries.CountAsync(d => d.Status == NotificationStatus.Failed, ct);
        return TypedResults.Ok(new NotificationSummaryDto(total, sent24, failed));
    }

    private static async Task<Ok<List<NotificationDeliveryDto>>> GetRecent(
        NotificationsDbContext db, CancellationToken ct)
    {
        var rows = await db.Deliveries.AsNoTracking()
            .OrderByDescending(d => d.OccurredAt).Take(50)
            .Select(d => new { d.Id, d.Channel, d.Recipient, d.Subject, d.Status, d.Error, d.OccurredAt })
            .ToListAsync(ct);
        // Mask in memory — the operator list identifies a recipient without dumping full PII.
        var dtos = rows.Select(d => new NotificationDeliveryDto(
            d.Id, d.Channel, MaskRecipient(d.Recipient), d.Subject, d.Status.ToString(), d.Error, d.OccurredAt)).ToList();
        return TypedResults.Ok(dtos);
    }

    private static string MaskRecipient(string recipient)
    {
        var at = recipient.IndexOf('@');
        if (at <= 1)
        {
            return recipient.Length <= 2 ? recipient : recipient[0] + "***";
        }

        return recipient[0] + "***" + recipient[at..];
    }
}

public record NotificationSummaryDto(int Total, int Sent24h, int Failed);
public record NotificationDeliveryDto(Guid Id, string Channel, string Recipient, string Subject, string Status, string? Error, DateTimeOffset OccurredAt);
