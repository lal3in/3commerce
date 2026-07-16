using System.Security.Claims;
using MassTransit;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure;
using ThreeCommerce.Ordering.Infrastructure.Sagas;

namespace ThreeCommerce.Ordering.Api.Endpoints;

public static class OrdersEndpoints
{
    public static IEndpointRouteBuilder MapOrders(this IEndpointRouteBuilder app)
    {
        var mine = app.MapGroup("/orders").WithTags("Orders").RequireAuthorization(InternalClaimsAuth.CustomerPolicy);
        mine.MapGet("/", ListMyOrders);
        mine.MapGet("/{id:guid}", GetMyOrder);

        // Order status is also readable anonymously by id — the confirmation page polls it.
        // (A signed link would scope this in production; acceptable for v1 status polling.)
        app.MapGet("/orders/{id:guid}/status", GetStatus).WithTags("Orders");

        // Admin order list/detail (operator console).
        var admin = app.MapGroup("/admin/orders").WithTags("Admin")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);
        admin.MapGet("/", ListAllOrders);
        admin.MapGet("/{id:guid}", GetAnyOrder);
        admin.MapPost("/{id:guid}/cancel", CancelOrder);

        // Checkout-saga monitor (mc_proc_1): counts by saga state so Mission Control can show how many
        // checkouts are in-flight (AwaitingPayment) vs. concluded (Confirmed/Cancelled).
        app.MapGet("/admin/checkouts", CheckoutStateCounts).WithTags("Admin")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);
        return app;
    }

    private static async Task<Ok<List<CheckoutStateCountDto>>> CheckoutStateCounts(OrderingDbContext db, CancellationToken ct)
    {
        var counts = await db.Set<CheckoutState>().AsNoTracking()
            .GroupBy(s => s.CurrentState)
            .Select(g => new CheckoutStateCountDto(g.Key, g.Count()))
            .ToListAsync(ct);
        return TypedResults.Ok(counts);
    }

    private static async Task<Results<Accepted, NotFound, Conflict<string>>> CancelOrder(
        Guid id, CancelOrderRequest? request, OrderingDbContext db, IPublishEndpoint publisher,
        IAuditRecorder audit, ClaimsPrincipal user, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == id, ct);
        if (order is null)
        {
            return TypedResults.NotFound();
        }

        if (order.Status == OrderStatus.Cancelled)
        {
            return TypedResults.Conflict("Order is already cancelled.");
        }

        if (order.Status == OrderStatus.Confirmed)
        {
            return TypedResults.Conflict("Confirmed (paid) orders can't be cancelled — issue a refund instead.");
        }

        // The OrderStatusConsumer transitions the (unpaid) order to Cancelled.
        var reason = request?.Reason ?? "cancelled by admin";
        await publisher.Publish(new OrderCancelled(id, reason), ct);
        await audit.RecordAsync(user.Mutation(
            order.TenantId, "Order", order.Id.ToString(), "ordering.order.cancel", reason), ct);
        await db.SaveChangesAsync(ct); // flush the bus outbox (OrderCancelled + AuditEntryRecorded)
        return TypedResults.Accepted($"/admin/orders/{id}");
    }

    private static async Task<Ok<List<OrderSummary>>> ListMyOrders(
        ClaimsPrincipal user, OrderingDbContext db, CancellationToken ct)
    {
        var uid = Guid.Parse(user.FindFirstValue("sub")!);
        var orders = await db.Orders.AsNoTracking()
            .Where(o => o.UserId == uid)
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => new OrderSummary(o.Id, o.Status.ToString(), o.GrossMinor, o.Currency, o.CreatedAt))
            .ToListAsync(ct);
        return TypedResults.Ok(orders);
    }

    private static async Task<Results<Ok<OrderDetail>, NotFound>> GetMyOrder(
        Guid id, ClaimsPrincipal user, OrderingDbContext db, CancellationToken ct)
    {
        var uid = Guid.Parse(user.FindFirstValue("sub")!);
        var order = await db.Orders.AsNoTracking().Include(o => o.Lines)
            .SingleOrDefaultAsync(o => o.Id == id && o.UserId == uid, ct);
        return order is null ? TypedResults.NotFound() : TypedResults.Ok(ToDetail(order));
    }

    private static async Task<Results<Ok<OrderStatusResponse>, NotFound>> GetStatus(
        Guid id, OrderingDbContext db, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == id, ct);
        if (order is not null)
        {
            return TypedResults.Ok(new OrderStatusResponse(order.Id, order.Status.ToString()));
        }

        var attempt = await db.CheckoutAttempts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct);
        return attempt is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(new OrderStatusResponse(attempt.Id, attempt.Status.ToString()));
    }

    private static async Task<Ok<List<OrderSummary>>> ListAllOrders(
        OrderingDbContext db, string? status, CancellationToken ct)
    {
        var query = db.Orders.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<ThreeCommerce.Ordering.Domain.OrderStatus>(status, out var s))
        {
            query = query.Where(o => o.Status == s);
        }

        var orders = await query.OrderByDescending(o => o.CreatedAt).Take(200)
            .Select(o => new OrderSummary(o.Id, o.Status.ToString(), o.GrossMinor, o.Currency, o.CreatedAt))
            .ToListAsync(ct);
        return TypedResults.Ok(orders);
    }

    private static async Task<Results<Ok<OrderDetail>, NotFound>> GetAnyOrder(
        Guid id, OrderingDbContext db, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, ct);
        return order is null ? TypedResults.NotFound() : TypedResults.Ok(ToDetail(order));
    }

    private static OrderDetail ToDetail(ThreeCommerce.Ordering.Domain.Order o) => new(
        o.Id, o.Status.ToString(), o.Email, o.NetMinor, o.ShippingMinor, o.DiscountMinor, o.TaxMinor, o.GrossMinor, o.Currency, o.CreatedAt,
        o.Lines.Select(l => new OrderLineResponse(l.ProductId, l.VariantId, l.VariantSku, l.Title, l.UnitPriceMinor, l.DiscountMinor, l.Quantity, l.FulfilmentType.ToString(), l.BillingMode.ToString())).ToList());
}

public record OrderSummary(Guid Id, string Status, long GrossMinor, string Currency, DateTimeOffset CreatedAt);
public record OrderLineResponse(Guid ProductId, Guid? VariantId, string? VariantSku, string Title, long UnitPriceMinor, long DiscountMinor, int Quantity, string FulfilmentType, string BillingMode);
public record OrderDetail(Guid Id, string Status, string Email, long NetMinor, long ShippingMinor, long DiscountMinor, long TaxMinor, long GrossMinor, string Currency, DateTimeOffset CreatedAt, List<OrderLineResponse> Lines);
public record OrderStatusResponse(Guid Id, string Status);
public record CancelOrderRequest(string? Reason);
public record CheckoutStateCountDto(string State, int Count);
