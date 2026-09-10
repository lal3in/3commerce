using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Tests;

/// <summary>
/// The per-line refund basis (rma_disc): an order's WHOLE discount split across its lines, so Support
/// can value a return at what the line was sold for rather than at its shelf price. Pure arithmetic —
/// the invariant is that the parts sum to the order's discount exactly, in every shape.
/// </summary>
public class OrderLineDiscountsTests
{
    [Fact]
    public void A_uniform_storefront_discount_spreads_by_line_value()
    {
        // No promotion allocation at all — the whole 2700 is the storefront-wide percentage, so it lands
        // in proportion to line value: 4000/9000 and 5000/9000 of 2700 = 1200 and 1500.
        var allocated = OrderLineDiscounts.For([4_000, 5_000], [0, 0], 2_700);

        Assert.Equal([1_200, 1_500], allocated);
        Assert.Equal(2_700, allocated.Sum());
    }

    [Fact]
    public void A_product_scoped_promotion_stays_on_the_line_it_covers()
    {
        // The promotion took 900 off the FIRST line only, and there is no storefront-wide part. Spreading
        // it proportionally instead would move money onto a line the promotion never touched.
        var allocated = OrderLineDiscounts.For([4_000, 5_000], [900, 0], 900);

        Assert.Equal([900, 0], allocated);
    }

    [Fact]
    public void A_promotion_and_a_storefront_discount_stack_without_losing_a_cent()
    {
        // 900 of product-scoped promotion on line 1, plus a 10% storefront-wide 900 spread by value
        // (400 / 500). Total 1800, and it lands as 1300 + 500.
        var allocated = OrderLineDiscounts.For([4_000, 5_000], [900, 0], 1_800);

        Assert.Equal([1_300, 500], allocated);
        Assert.Equal(1_800, allocated.Sum());
    }

    [Fact]
    public void The_subtotal_clamp_never_allocates_more_than_the_order_actually_discounted()
    {
        // Checkout clamps promotion + storefront discount at the subtotal, so the recorded per-line
        // promotion vector can sum to MORE than the order's real discount. The allocation must follow the
        // order, not the vector — otherwise a refund gives back less than the shopper is owed.
        var allocated = OrderLineDiscounts.For([1_000, 1_000], [1_500, 500], 2_000);

        Assert.Equal(2_000, allocated.Sum());
        Assert.Equal([1_500, 500], allocated);
    }

    [Fact]
    public void Rounding_remainders_are_distributed_so_the_parts_sum_exactly()
    {
        // 1000 over three equal lines is 333.33 each; largest remainder puts the stray cent somewhere
        // rather than losing it.
        var allocated = OrderLineDiscounts.For([1_000, 1_000, 1_000], [0, 0, 0], 1_000);

        Assert.Equal(1_000, allocated.Sum());
        Assert.All(allocated, part => Assert.InRange(part, 333, 334));
    }

    [Fact]
    public void An_undiscounted_order_allocates_nothing()
    {
        Assert.Equal([0, 0], OrderLineDiscounts.For([4_000, 5_000], [0, 0], 0));
        Assert.Empty(OrderLineDiscounts.For([], [], 500));
    }
}
