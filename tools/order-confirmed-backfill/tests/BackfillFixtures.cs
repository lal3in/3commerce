using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.BuildingBlocks.Infrastructure.Tenancy;
using ThreeCommerce.Fulfillment.Infrastructure;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure;
using ThreeCommerce.Workers.Notifications.Domain;
using ThreeCommerce.Workers.Notifications.Infrastructure;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill.Tests;

/// <summary>The EF in-memory provider has no RLS settings to apply; the scope passes straight through.</summary>
internal sealed class PassThroughTenantScope : ITenantScope
{
    public Task<T> RunAsync<T>(DbContext db, TenantContext context, Func<Task<T>> work, CancellationToken ct) => work();
}

internal static class BackfillFixtures
{
    public static readonly DateTimeOffset T0 = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

    public static OrderingDbContext OrderingDb(string name) =>
        new(new DbContextOptionsBuilder<OrderingDbContext>().UseInMemoryDatabase(name).Options);

    public static FulfillmentDbContext FulfillmentDb(string name) =>
        new(new DbContextOptionsBuilder<FulfillmentDbContext>().UseInMemoryDatabase(name).Options);

    public static NotificationsDbContext NotificationsDb(string name) =>
        new(new DbContextOptionsBuilder<NotificationsDbContext>().UseInMemoryDatabase(name).Options);

    public static OrderFacts Facts(
        string email,
        DateTimeOffset confirmedAt,
        Guid? tenant = null,
        OrderStatus status = OrderStatus.Confirmed,
        bool disputed = false,
        bool shippable = true) =>
        new(Guid.CreateVersion7(), tenant ?? Guid.Empty, 1000, status, disputed, email, confirmedAt, shippable);

    public static DeliveryFact Sent(string recipient, DateTimeOffset at, string? reference = null) =>
        new(recipient, NotificationStatus.Sent, at, reference);

    /// <summary>A confirmed order with lines of the given supply types (one unit, 10.00 each).</summary>
    public static Order MakeOrder(Guid tenant, DateTimeOffset createdAt, string email, OrderStatus status, params FulfilmentType[] lines)
    {
        var id = Guid.CreateVersion7();
        return new Order
        {
            Id = id,
            TenantId = tenant,
            StorefrontId = Guid.NewGuid(),
            PublicOrderNumber = 1000,
            Email = email,
            Status = status,
            GrossMinor = 1000 * lines.Length,
            Currency = "AUD",
            ShipName = "Buyer",
            ShipLine1 = "1 St",
            ShipCity = "Sydney",
            ShipPostcode = "2000",
            ShipCountry = "AU",
            CreatedAt = createdAt,
            Lines = lines.Select(t => new OrderLine
            {
                Id = Guid.CreateVersion7(),
                OrderId = id,
                ProductId = Guid.NewGuid(),
                Title = t.ToString(),
                Quantity = 1,
                UnitPriceMinor = 1000,
                FulfilmentType = t,
            }).ToList(),
        };
    }
}
