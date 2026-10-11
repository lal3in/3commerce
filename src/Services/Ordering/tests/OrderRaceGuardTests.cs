using MassTransit;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure;
using ThreeCommerce.Ordering.Infrastructure.Consumers;

namespace ThreeCommerce.Ordering.Tests;

/// <summary>
/// Static guards for the order-write race fixes (order_number_and_edit_races_2026_10_11). The behaviour itself is
/// proven against Postgres + RabbitMQ in the integration lane (OrderNumberAllocationTests, OrderEditConcurrencyTests);
/// these pin the wiring that makes it work, so a refactor cannot silently undo it.
/// </summary>
public class OrderRaceGuardTests
{
    [Fact]
    public void Order_number_allocator_uses_its_own_pool_and_never_enlists()
    {
        const string Service = "Host=db;Database=ordering_db;Username=ordering_svc;Password=pw;Maximum Pool Size=10";

        var allocator = new NpgsqlConnectionStringBuilder(OrderNumberAllocator.AllocatorConnectionString(Service));

        // A different Application Name is a different Npgsql pool: consumers holding every main-pool connection
        // cannot starve the allocation they are waiting on.
        Assert.Equal(OrderNumberAllocator.ApplicationNameSuffix, allocator.ApplicationName);
        Assert.False(allocator.Enlist);
        Assert.Equal("ordering_db", allocator.Database);
        Assert.Equal("pw", allocator.Password);
        Assert.Equal(10, allocator.MaxPoolSize);
    }

    [Fact]
    public void Order_number_allocator_keeps_an_existing_application_name_recognisable()
    {
        var allocator = new NpgsqlConnectionStringBuilder(
            OrderNumberAllocator.AllocatorConnectionString("Host=db;Database=d;Application Name=ordering"));

        Assert.Equal("ordering/order-numbers", allocator.ApplicationName);
    }

    [Fact]
    public void Order_version_is_the_xmin_concurrency_token()
    {
        using var db = new OrderingDbContext(new DbContextOptionsBuilder<OrderingDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);

        var version = db.Model.FindEntityType(typeof(Order))!.FindProperty(nameof(Order.Version))!;

        Assert.True(version.IsConcurrencyToken);
        Assert.Equal("xmin", version.GetColumnName());
        Assert.Equal("xid", version.GetColumnType());
    }

    [Fact]
    public void Guest_attach_is_applied_by_the_order_status_consumer_on_the_order_partition()
    {
        // GuestOrderOwnerVerified must be consumed where the order's other events are (same endpoint, same
        // partitioner), not by the EmailVerified sweep, which is keyed by email and cannot share the partition.
        Assert.True(typeof(IConsumer<GuestOrderOwnerVerified>).IsAssignableFrom(typeof(OrderStatusConsumer)));
        Assert.False(typeof(IConsumer<GuestOrderOwnerVerified>).IsAssignableFrom(typeof(GuestOrderAttachConsumer)));
    }
}
