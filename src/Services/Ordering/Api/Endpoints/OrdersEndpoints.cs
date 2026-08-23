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

        // Supplier portal: the orders a supplier fulfils (its offers' SupplierId on order lines) and the
        // "mark delivered" action. SupplierPolicy also admits operators (admin/master); the handlers
        // self-scope a supplier login to its own supplier_entity (never a client-supplied id).
        var supplier = app.MapGroup("/orders/supplier").WithTags("Supplier Orders")
            .RequireAuthorization(InternalClaimsAuth.SupplierPolicy);
        supplier.MapGet("/me", ListSupplierOrders);
        supplier.MapPost("/{id:guid}/mark-delivered", MarkDelivered);

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

    // The orders a supplier fulfils: any order carrying a line whose SupplierId is this supplier's entity id.
    // A supplier login is scoped to its own supplier_entity claim; an operator (admin/master) with no binding
    // gets an empty list here (they use the admin order surfaces). Most-recent first, capped.
    private static async Task<Results<Ok<List<SupplierOrderSummary>>, NotFound>> ListSupplierOrders(
        ClaimsPrincipal user, OrderingDbContext db, CancellationToken ct)
    {
        var supplierId = InternalClaimsAuth.SupplierEntityId(user);
        if (supplierId is null)
        {
            return TypedResults.NotFound();
        }

        var orders = await db.Orders.AsNoTracking()
            .Where(o => o.Lines.Any(l => l.SupplierId == supplierId.Value))
            .OrderByDescending(o => o.CreatedAt)
            .Take(200)
            .Select(o => new SupplierOrderSummary(
                o.Id, o.PublicOrderNumber, o.Status.ToString(), o.GrossMinor, o.Currency, o.CreatedAt,
                o.CollectAtWarehouse, o.Email,
                o.Lines.Where(l => l.SupplierId == supplierId.Value)
                    .Select(l => new SupplierOrderLine(l.Title, l.Quantity, l.FulfilmentType.ToString())).ToList()))
            .ToListAsync(ct);
        return TypedResults.Ok(orders);
    }

    // A supplier (or an operator) marks one of its orders delivered: Confirmed → Delivered, then publish
    // OrderDelivered so Fulfillment closes the order's shipments and Notifications sends the confirmation.
    // Authorized to the FULFILLING supplier only (a line SupplierId matching the caller's supplier_entity)
    // or an operator (admin/master); any other supplier is forbidden even if signed in. Idempotent: an
    // already-Delivered order returns 200 with no second event.
    private static async Task<Results<Ok<OrderStatusResponse>, NotFound, ForbidHttpResult, Conflict<string>>> MarkDelivered(
        Guid id, ClaimsPrincipal user, OrderingDbContext db, IPublishEndpoint publisher,
        IAuditRecorder audit, CancellationToken ct)
    {
        var order = await db.Orders.Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, ct);
        if (order is null)
        {
            return TypedResults.NotFound();
        }

        // Only a supplier that actually fulfils a line on this order (or an operator) may mark it delivered.
        var isOperator = user.IsInRole("admin") || user.IsInRole(InternalClaimsAuth.MasterRole);
        var supplierId = InternalClaimsAuth.SupplierEntityId(user);
        var fulfilsALine = supplierId is { } sid && order.Lines.Any(l => l.SupplierId == sid);
        if (!isOperator && !fulfilsALine)
        {
            return TypedResults.Forbid();
        }

        if (order.Status == OrderStatus.Delivered)
        {
            return TypedResults.Ok(new OrderStatusResponse(order.Id, order.Status.ToString())); // idempotent
        }

        if (order.Status != OrderStatus.Confirmed)
        {
            return TypedResults.Conflict("Only a confirmed order can be marked delivered.");
        }

        order.Status = OrderStatus.Delivered;
        var actingSupplier = supplierId ?? Guid.Empty;
        await publisher.Publish(new OrderDelivered(order.Id, order.TenantId, actingSupplier), ct);
        await audit.RecordAsync(user.Mutation(
            order.TenantId, "Order", order.Id.ToString(), "ordering.order.mark_delivered", $"#{order.PublicOrderNumber}"), ct);
        await db.SaveChangesAsync(ct); // flush the bus outbox (OrderDelivered + AuditEntryRecorded)
        return TypedResults.Ok(new OrderStatusResponse(order.Id, order.Status.ToString()));
    }

    private static async Task<Ok<List<OrderSummary>>> ListMyOrders(
        ClaimsPrincipal user, OrderingDbContext db, CancellationToken ct)
    {
        var uid = Guid.Parse(user.FindFirstValue("sub")!);
        var orders = await db.Orders.AsNoTracking()
            .Where(o => o.UserId == uid)
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => new OrderSummary(o.Id, o.Status.ToString(), o.GrossMinor, o.Currency, o.CreatedAt,
                string.IsNullOrEmpty(o.PaymentOption) ? "CreditCard" : o.PaymentOption, o.StorefrontId, o.PartiallyRefunded, o.Disputed))
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
        OrderingDbContext db, string? status, Guid? storefrontId, string? product, DateOnly? start, DateOnly? end, CancellationToken ct)
    {
        var query = db.Orders.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<ThreeCommerce.Ordering.Domain.OrderStatus>(status, out var s))
        {
            query = query.Where(o => o.Status == s);
        }

        if (storefrontId is { } sid)
        {
            query = query.Where(o => o.StorefrontId == sid);
        }

        // Product filter: one term matching a line by product id OR title. A GUID term matches the exact
        // ProductId; any other term is a case-insensitive substring match on the line Title. Uses Any() →
        // an EXISTS over the order's lines, so an order matches when any of its lines does.
        if (!string.IsNullOrWhiteSpace(product))
        {
            var term = product.Trim();
            if (Guid.TryParse(term, out var pid))
            {
                query = query.Where(o => o.Lines.Any(l => l.ProductId == pid));
            }
            else
            {
                query = query.Where(o => o.Lines.Any(l => EF.Functions.ILike(l.Title, $"%{term}%")));
            }
        }

        // Inclusive calendar-day range on CreatedAt (UTC): end covers the whole day. Applied before the
        // 200-row cap so the filter spans the whole order history, not just the loaded page.
        if (start is { } from)
        {
            var fromInstant = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(o => o.CreatedAt >= fromInstant);
        }

        if (end is { } to)
        {
            var toExclusive = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(o => o.CreatedAt < toExclusive);
        }

        var orders = await query.OrderByDescending(o => o.CreatedAt).Take(200)
            .Select(o => new OrderSummary(o.Id, o.Status.ToString(), o.GrossMinor, o.Currency, o.CreatedAt,
                string.IsNullOrEmpty(o.PaymentOption) ? "CreditCard" : o.PaymentOption, o.StorefrontId, o.PartiallyRefunded, o.Disputed))
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
        o.Lines.Select(l => new OrderLineResponse(l.ProductId, l.VariantId, l.VariantSku, l.Title, l.UnitPriceMinor, l.DiscountMinor, l.Quantity, l.FulfilmentType.ToString(), l.BillingMode.ToString())).ToList(),
        o.PublicOrderNumber, o.PaymentOption, o.PaymentInstrumentSummary, o.PaymentProvider, o.PartiallyRefunded, o.Disputed,
        new ShippingAddressResponse(o.ShipName, o.ShipLine1, o.ShipCity, o.ShipPostcode, o.ShipCountry));
}

// PaymentOption defaults for legacy rows: the column is NOT NULL with a "CreditCard" DB default,
// but guard against empty strings so consumers always get a real option name.
public record OrderSummary(Guid Id, string Status, long GrossMinor, string Currency, DateTimeOffset CreatedAt, string PaymentOption = "CreditCard", Guid? StorefrontId = null, bool PartiallyRefunded = false, bool Disputed = false);
public record OrderLineResponse(Guid ProductId, Guid? VariantId, string? VariantSku, string Title, long UnitPriceMinor, long DiscountMinor, int Quantity, string FulfilmentType, string BillingMode);
public record ShippingAddressResponse(string Name, string Line1, string City, string Postcode, string Country);
public record OrderDetail(
    Guid Id, string Status, string Email, long NetMinor, long ShippingMinor, long DiscountMinor, long TaxMinor, long GrossMinor,
    string Currency, DateTimeOffset CreatedAt, List<OrderLineResponse> Lines,
    long PublicOrderNumber = 0, string PaymentOption = "CreditCard", string? PaymentInstrumentSummary = null,
    string PaymentProvider = "Stripe", bool PartiallyRefunded = false, bool Disputed = false, ShippingAddressResponse? ShippingAddress = null);
public record OrderStatusResponse(Guid Id, string Status);
public record CancelOrderRequest(string? Reason);
public record SupplierOrderLine(string Title, int Quantity, string FulfilmentType);
public record SupplierOrderSummary(
    Guid Id, long PublicOrderNumber, string Status, long GrossMinor, string Currency, DateTimeOffset CreatedAt,
    bool CollectAtWarehouse, string Email, List<SupplierOrderLine> Lines);
public record CheckoutStateCountDto(string State, int Count);
