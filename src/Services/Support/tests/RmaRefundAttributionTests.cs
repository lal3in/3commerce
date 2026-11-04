using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Support.Domain;
using ThreeCommerce.Support.Infrastructure.Sagas;

namespace ThreeCommerce.Support.Tests;

/// <summary>
/// ADR-0063 P5: an RMA refund names the order lines its money is for, so a credit note can credit them line by line.
/// The shares are the lines' DISCOUNTED values (ADR-0055) and always sum to the amount requested — also when the order's
/// refundable-gross cap made the request smaller than the lines are worth. A request that cannot be attributed goes out
/// amount-only, exactly as before P5.
/// </summary>
public class RmaRefundAttributionTests
{
    private static readonly Guid Keyboard = Guid.Parse("01920000-0000-7000-8000-00000000000a");
    private static readonly Guid Lamp = Guid.Parse("01920000-0000-7000-8000-00000000000b");
    private static readonly Guid Cable = Guid.Parse("01920000-0000-7000-8000-00000000000c");

    private static RmaRequestLine Line(Guid orderLineId, int qty, long unit, long discount = 0) => new()
    {
        Id = Guid.CreateVersion7(),
        RmaId = Guid.Empty,
        OrderLineId = orderLineId,
        ProductId = Guid.CreateVersion7(),
        Title = "Item",
        Quantity = qty,
        UnitPriceMinor = unit,
        DiscountMinor = discount,
    };

    [Fact]
    public void An_uncapped_request_attributes_each_line_its_discounted_value()
    {
        // Keyboard 2 × 5000 less 700 discount = 9300; lamp 1 × 1900 = 1900.
        var shares = RmaRefundAttribution.For([Line(Keyboard, 2, 5000, 700), Line(Lamp, 1, 1900)], 11200);

        Assert.Equal(
            [new RmaRefundLineShare(Keyboard, 2, 9300), new RmaRefundLineShare(Lamp, 1, 1900)],
            shares);
    }

    [Fact]
    public void A_capped_request_spreads_the_amount_by_value_and_still_sums_exactly()
    {
        // Three equal lines worth 1000 each, but only 1000 of the order is still refundable: 333.33 each, the one
        // leftover minor unit to the earliest line (equal remainders).
        var shares = RmaRefundAttribution.For([Line(Keyboard, 1, 1000), Line(Lamp, 1, 1000), Line(Cable, 1, 1000)], 1000)!;

        Assert.Equal([334L, 333L, 333L], shares.Select(s => s.AmountMinor));
        Assert.Equal(1000, shares.Sum(s => s.AmountMinor));
        Assert.Equal([Keyboard, Lamp, Cable], shares.Select(s => s.OrderLineId));
    }

    [Fact]
    public void A_capped_request_gives_the_leftover_to_the_largest_remainder_and_never_exceeds_a_lines_value()
    {
        // 700 + 300 = 1000 worth, 999 refundable: exact 699.3 / 299.7 → floors 699 + 299 = 998, the leftover unit to the
        // lamp (remainder .7 > .3).
        var shares = RmaRefundAttribution.For([Line(Keyboard, 1, 700), Line(Lamp, 1, 300)], 999)!;

        Assert.Equal([699L, 300L], shares.Select(s => s.AmountMinor));
    }

    [Fact]
    public void Random_capped_requests_always_sum_to_the_amount_within_each_lines_value()
    {
        var random = new Random(20261029);
        for (var run = 0; run < 2000; run++)
        {
            var lines = Enumerable.Range(0, random.Next(1, 6))
                .Select(_ => Line(Guid.CreateVersion7(), random.Next(1, 5), random.Next(0, 20_000), 0))
                .ToList();
            var worth = lines.Sum(l => l.RefundableMinor());
            if (worth == 0)
            {
                continue;
            }

            var amount = random.NextInt64(1, worth + 1);
            var shares = RmaRefundAttribution.For(lines, amount)!;

            Assert.Equal(amount, shares.Sum(s => s.AmountMinor));
            Assert.All(shares.Zip(lines), p => Assert.InRange(p.First.AmountMinor, 0, p.Second.RefundableMinor()));
        }
    }

    [Fact]
    public void A_request_that_cannot_be_attributed_goes_out_amount_only()
    {
        // Negative controls: a line from a snapshot projected before P5 has no order-line id; no lines at all; lines worth
        // less than the amount (never produced by the endpoints); and a zero amount.
        Assert.Null(RmaRefundAttribution.For([Line(Keyboard, 1, 1000), Line(Guid.Empty, 1, 1000)], 2000));
        Assert.Null(RmaRefundAttribution.For([], 2000));
        Assert.Null(RmaRefundAttribution.For([Line(Keyboard, 1, 1000)], 1001));
        Assert.Null(RmaRefundAttribution.For([Line(Keyboard, 1, 1000)], 0));
    }

    [Fact]
    public void The_rma_refund_request_carries_its_lines_no_shipping_and_kind_return()
    {
        var saga = Saga(amount: 11200);

        var request = RequestRmaRefundActivity.Build(saga, [Line(Keyboard, 2, 5000, 700), Line(Lamp, 1, 1900)]);

        Assert.Equal((saga.RefundId, saga.OrderId, 11200L, "damaged", "rma"), (request.RefundId, request.OrderId, request.AmountMinor, request.Reason, request.RequestedBy));
        Assert.Equal([new RefundLine(Keyboard, 2, 9300), new RefundLine(Lamp, 1, 1900)], request.Lines!);
        Assert.Equal(0, request.ShippingMinor);
        Assert.Equal(RefundKind.Return, request.Kind);
        Assert.Equal(request.AmountMinor, request.Lines!.Sum(l => l.AmountMinor) + (request.ShippingMinor ?? 0));
    }

    [Fact]
    public void A_legacy_rma_still_refunds_the_same_amount_without_lines()
    {
        var saga = Saga(amount: 2000);

        var request = RequestRmaRefundActivity.Build(saga, [Line(Guid.Empty, 2, 1000)]);

        Assert.Equal(2000, request.AmountMinor);
        Assert.Null(request.Lines);
        Assert.Null(request.ShippingMinor);
        Assert.Equal(RefundKind.Return, request.Kind);
    }

    private static RmaState Saga(long amount) => new()
    {
        CorrelationId = Guid.CreateVersion7(),
        CurrentState = "Requested",
        OrderId = Guid.CreateVersion7(),
        RefundId = Guid.CreateVersion7(),
        AmountMinor = amount,
        Reason = "damaged",
    };
}
