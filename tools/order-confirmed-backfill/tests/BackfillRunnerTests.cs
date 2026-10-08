using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure;
using ThreeCommerce.Workers.Notifications.Domain;
using ThreeCommerce.Workers.Notifications.Email;
using ThreeCommerce.Workers.Notifications.Infrastructure;
using static ThreeCommerce.Tools.OrderConfirmedBackfill.Tests.BackfillFixtures;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill.Tests;

/// <summary>
/// The whole read side over the three services' real DbContexts (in-memory provider): selection per target, the
/// tenant filter, and the re-run property — once the consumers have done their part, a second run selects nothing.
/// </summary>
public class BackfillRunnerTests
{
    private static readonly Guid TenantA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    private sealed class Stack(string name)
    {
        public string Name { get; } = name;

        public BackfillRunner Runner() => new(
            new OrderSource(OrderingDb(Name), new PassThroughTenantScope()),
            new FulfillmentSource(FulfillmentDb(Name), new PassThroughTenantScope()),
            new DeliverySource(NotificationsDb(Name)));

        public async Task AddOrdersAsync(params Order[] orders)
        {
            await using var db = OrderingDb(Name);
            db.Orders.AddRange(orders);
            await db.SaveChangesAsync();
        }

        public async Task ShipAsync(Order order)
        {
            await using var db = FulfillmentDb(Name);
            db.Shipments.Add(new Shipment { Id = Guid.CreateVersion7(), TenantId = order.TenantId, OrderId = order.Id, FulfillmentSource = "Warehouse" });
            await db.SaveChangesAsync();
        }

        public async Task HoldAsync(Order order)
        {
            await using var db = FulfillmentDb(Name);
            db.HeldOrders.Add(new HeldOrder { Id = Guid.CreateVersion7(), TenantId = order.TenantId, OrderId = order.Id, PayloadJson = "{}" });
            await db.SaveChangesAsync();
        }

        /// <summary>A pre-fix delivery row: recipient + subject + time, no reference.</summary>
        public async Task LegacyEmailAsync(string to, DateTimeOffset at)
        {
            await using var db = NotificationsDb(Name);
            db.Deliveries.Add(new NotificationDelivery
            {
                Id = Guid.CreateVersion7(),
                Recipient = to,
                Subject = DeliverySource.OrderConfirmedSubject,
                Status = NotificationStatus.Sent,
                OccurredAt = at,
            });
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// What the Notifications worker does with a (backfilled) OrderConfirmed: the live template through the live
        /// recording sender — which now writes the order reference to the delivery log.
        /// </summary>
        public async Task WorkerEmailsAsync(Order order)
        {
            var services = new ServiceCollection();
            services.AddDbContext<NotificationsDbContext>(o => Microsoft.EntityFrameworkCore.InMemoryDbContextOptionsExtensions.UseInMemoryDatabase(o, Name));
            await using var provider = services.BuildServiceProvider();
            var sender = new RecordingEmailSender(
                new LoggingEmailSender(NullLogger<LoggingEmailSender>.Instance),
                provider.GetRequiredService<IServiceScopeFactory>(),
                TimeProvider.System,
                NullLogger<RecordingEmailSender>.Instance);
            await sender.SendAsync(new EmailTemplates("http://localhost:3000").OrderConfirmed(order.Email, order.Id, order.GrossMinor, order.Currency), default);
        }
    }

    private static BackfillOptions Options(params string[] extra) =>
        BackfillOptions.Parse(["--target", "both", "--dry-run", .. extra]);

    [Fact]
    public async Task Selects_per_target_respects_the_tenant_filter_and_a_rerun_after_delivery_selects_nothing()
    {
        var stack = new Stack(nameof(Selects_per_target_respects_the_tenant_filter_and_a_rerun_after_delivery_selects_nothing));
        await stack.LegacyEmailAsync("seed@x.test", T0.AddDays(-1)); // the log existed before all of these orders

        var shippedAndEmailed = MakeOrder(TenantA, T0, "a@x.test", OrderStatus.Confirmed, FulfilmentType.Warehouse);
        var missedByBoth = MakeOrder(TenantA, T0.AddMinutes(1), "b@x.test", OrderStatus.Confirmed, FulfilmentType.Dropship, FulfilmentType.DigitalDownload);
        var heldAndEmailed = MakeOrder(TenantA, T0.AddMinutes(2), "c@x.test", OrderStatus.Confirmed, FulfilmentType.Unassigned);
        var digitalOnly = MakeOrder(TenantA, T0.AddMinutes(3), "d@x.test", OrderStatus.Confirmed, FulfilmentType.DigitalDownload);
        var refunded = MakeOrder(TenantA, T0.AddMinutes(4), "e@x.test", OrderStatus.Refunded, FulfilmentType.Warehouse);
        // One address, two tenants, two orders half a second apart, ONE email: either order's. Matching only tenant A's
        // orders would wrongly give the email to sharedA; matching all tenants keeps it Ambiguous (not re-emailed).
        var sharedA = MakeOrder(TenantA, T0.AddMinutes(10), "shared@x.test", OrderStatus.Confirmed, FulfilmentType.Warehouse);
        var sharedB = MakeOrder(TenantB, T0.AddMinutes(10).AddMilliseconds(500), "shared@x.test", OrderStatus.Confirmed, FulfilmentType.Warehouse);
        await stack.AddOrdersAsync(shippedAndEmailed, missedByBoth, heldAndEmailed, digitalOnly, refunded, sharedA, sharedB);

        await stack.ShipAsync(shippedAndEmailed);
        await stack.HoldAsync(heldAndEmailed);
        await stack.ShipAsync(sharedA);
        await stack.WorkerEmailsAsync(shippedAndEmailed);                    // post-fix: referenced
        await stack.LegacyEmailAsync("C@x.test", T0.AddMinutes(2).AddMilliseconds(300)); // pre-fix: time-matched
        await stack.LegacyEmailAsync("shared@x.test", T0.AddMinutes(10).AddMilliseconds(800));

        var options = Options("--tenant", TenantA.ToString());
        var plan = (await stack.Runner().PlanAsync(options, default)).Plan;

        Assert.Equal([missedByBoth.Id], plan.Fulfillment.Select(o => o.OrderId));
        Assert.Equal(new[] { missedByBoth.Id, digitalOnly.Id }.Order(), plan.Notifications.Select(o => o.OrderId).Order());
        Assert.Equal(1, plan.Skipped[SkipReason.Refunded]);
        Assert.Equal(EmailEvidence.EmailedByReference, plan.Email.ByOrder[shippedAndEmailed.Id]);
        Assert.Equal(EmailEvidence.EmailedByTimeMatch, plan.Email.ByOrder[heldAndEmailed.Id]);
        Assert.Equal(EmailEvidence.Ambiguous, plan.Email.ByOrder[sharedA.Id]);
        Assert.DoesNotContain(plan.Fulfillment.Concat(plan.Notifications), o => o.TenantId == TenantB);

        // The consumers do their part for what was sent …
        await stack.ShipAsync(missedByBoth);
        await stack.WorkerEmailsAsync(missedByBoth);
        await stack.WorkerEmailsAsync(digitalOnly);

        // … and the re-run finds nothing left (and the report never prints an address).
        var rerun = await stack.Runner().PlanAsync(options, default);
        Assert.Empty(rerun.Plan.Fulfillment);
        Assert.Empty(rerun.Plan.Notifications);
        var report = new StringWriter();
        BackfillRunner.WriteReport(rerun, Options("--tenant", TenantA.ToString(), "--list"), report);
        Assert.DoesNotContain("@", report.ToString());
    }

    [Fact]
    public async Task The_rebuilt_event_round_trips_the_contract_serializer()
    {
        var stack = new Stack(nameof(The_rebuilt_event_round_trips_the_contract_serializer));
        var order = MakeOrder(TenantA, T0, "a@x.test", OrderStatus.Confirmed, FulfilmentType.Warehouse);
        await stack.AddOrdersAsync(order);

        var run = await stack.Runner().PlanAsync(Options(), default);
        var message = OrderConfirmedFactory.From(run.Orders[order.Id]);
        var roundTripped = JsonSerializer.Deserialize<OrderConfirmed>(JsonSerializer.Serialize(message))!;

        Assert.Equal(order.Id, roundTripped.OrderId);
        Assert.Equal(FulfilmentType.Warehouse, Assert.Single(roundTripped.Lines).FulfilmentType);
    }
}
