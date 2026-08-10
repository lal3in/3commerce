using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.Payments.Api.Endpoints;

/// <summary>
/// Per-storefront auto-billing schedule admin (jobmgr_2): each storefront's daily subscription auto-renew
/// time (default 12:00:00 server time) and on/off switch. The SubscriptionAutoRenewJob sweep reads these.
/// </summary>
public static class BillingScheduleEndpoints
{
    public static IEndpointRouteBuilder MapBillingSchedules(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/billing-schedules")
            .WithTags("Billing schedules")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);
        group.MapGet("/", List);
        group.MapPut("/{storefrontId:guid}", Configure);
        return app;
    }

    private static async Task<Ok<List<BillingScheduleDto>>> List(PaymentsDbContext db, CancellationToken ct)
    {
        var rows = await db.StorefrontBillingSchedules.AsNoTracking()
            .OrderBy(x => x.StorefrontId)
            .Select(x => new BillingScheduleDto(x.StorefrontId, x.DailyRunTime.ToString("HH:mm:ss"), x.Enabled, x.LastRunOn))
            .ToListAsync(ct);
        return TypedResults.Ok(rows);
    }

    private static async Task<Results<Ok<BillingScheduleDto>, BadRequest<string>>> Configure(
        Guid storefrontId, ConfigureBillingScheduleRequest request, PaymentsDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (!TimeOnly.TryParse(request.DailyRunTime, out var dailyRunTime))
        {
            return TypedResults.BadRequest("DailyRunTime must be a valid time (e.g. 12:00:00).");
        }

        var now = time.GetUtcNow();
        var schedule = await db.StorefrontBillingSchedules.SingleOrDefaultAsync(x => x.StorefrontId == storefrontId, ct);
        if (schedule is null)
        {
            schedule = StorefrontBillingSchedule.CreateDefault(storefrontId, now);
            db.StorefrontBillingSchedules.Add(schedule);
        }

        schedule.Configure(dailyRunTime, request.Enabled, now);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new BillingScheduleDto(schedule.StorefrontId, schedule.DailyRunTime.ToString("HH:mm:ss"), schedule.Enabled, schedule.LastRunOn));
    }
}

public record ConfigureBillingScheduleRequest([property: Required] string DailyRunTime, bool Enabled = true);
public record BillingScheduleDto(Guid StorefrontId, string DailyRunTime, bool Enabled, DateOnly? LastRunOn);
