using System.Text.Json;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure;

namespace ThreeCommerce.Ordering.Tests;

/// <summary>
/// An order holds its creation time at the precision Postgres stores it (whole microseconds). A Linux clock has 100 ns
/// ticks, which a timestamptz round trip drops — so without this the <c>OrderConfirmed.CapturedAt</c> published from the
/// in-memory order (<c>…2.5351758</c>) differs from the one the operator backfill rebuilds from the row
/// (<c>…2.535175</c>, ADR-0060; <c>OrderConfirmedBackfillTests</c> failed on CI with exactly that). The clock is pinned
/// to a sub-microsecond tick so the case is deterministic on macOS too (whose clock only gives microseconds).
/// </summary>
public class OrderStoredTimeTests
{
    // …02.5351758: the last tick digit (8 × 100 ns) is what Postgres drops.
    private static readonly DateTimeOffset LinuxNow = new DateTimeOffset(2026, 11, 5, 3, 4, 2, TimeSpan.Zero).AddTicks(5_351_758);
    private static readonly DateTimeOffset StoredNow = new DateTimeOffset(2026, 11, 5, 3, 4, 2, TimeSpan.Zero).AddTicks(5_351_750);

    [Fact]
    public void StoredTime_truncates_to_whole_microseconds()
    {
        Assert.Equal(StoredNow, StoredTime.Of(LinuxNow));
        Assert.Equal(StoredNow, StoredTime.Of(StoredNow)); // already stored precision: unchanged
    }

    [Fact]
    public void An_order_confirmed_at_a_sub_microsecond_tick_publishes_the_capture_time_the_backfill_rebuilds()
    {
        var order = Attempt().ToOrder(7, LinuxNow);

        Assert.Equal(StoredNow, order.CreatedAt);

        // The live event (from this in-memory order) carries what the row will hold — so the event the backfill
        // rebuilds from the reloaded row serializes identically.
        var live = OrderConfirmedFactory.From(order);
        Assert.Equal(StoredNow, live.CapturedAt);
        Assert.Contains("\"CapturedAt\":\"2026-11-05T03:04:02.535175+00:00\"", JsonSerializer.Serialize(live));
    }

    private static CheckoutAttempt Attempt()
    {
        var id = Guid.CreateVersion7();
        return new CheckoutAttempt
        {
            Id = id,
            TenantId = Guid.CreateVersion7(),
            StorefrontId = Guid.CreateVersion7(),
            Email = "buyer@example.test",
            Status = CheckoutAttemptStatus.AwaitingPayment,
            NetMinor = 1000,
            GrossMinor = 1000,
            Currency = "EUR",
            PaymentIntentId = "pi_fake",
            ShipName = "B",
            ShipLine1 = "1 St",
            ShipCity = "Berlin",
            ShipPostcode = "10115",
            ShipCountry = "DE",
            CreatedAt = StoredTime.Of(LinuxNow.AddMinutes(-2)),
            Lines =
            [
                new CheckoutAttemptLine
                {
                    Id = Guid.CreateVersion7(),
                    CheckoutAttemptId = id,
                    ProductId = Guid.CreateVersion7(),
                    Title = "Keyboard",
                    Quantity = 1,
                    UnitPriceMinor = 1000,
                    FulfilmentType = FulfilmentType.Warehouse,
                },
            ],
        };
    }
}
