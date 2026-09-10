using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Support.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// The refund BASIS (rma_disc): what an RMA is actually worth once the order was discounted.
/// <para>
/// Refunds used to be derived from the UNDISCOUNTED list price on the order snapshot, which moved real
/// money in two wrong directions: a partial return refunded more than the goods were sold for, and a
/// full return computed an amount above the captured gross, which Payments silently dropped — leaving
/// the RMA in RefundPending forever with no refund and no failure.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class RmaRefundBasisTests(Phase4Fixture fixture)
{
    private sealed record RmaCreated(Guid RmaId);

    // The audit's worked example: subtotal 9000, discount 2700, shipping 499, tax 578 → gross 7377.
    private const long Subtotal = 9_000;
    private const long Discount = 2_700;
    private const long Gross = 7_377;

    private HttpClient Customer()
    {
        var c = fixture.Support.CreateClient();
        c.DefaultRequestHeaders.Add(InternalClaimsAuth.HeaderName, fixture.Claims("customer"));
        return c;
    }

    private HttpClient Admin()
    {
        var c = fixture.Support.CreateClient();
        c.DefaultRequestHeaders.Add(InternalClaimsAuth.HeaderName, fixture.Claims("admin"));
        return c;
    }

    [Fact]
    public async Task A_full_return_of_a_discounted_order_refunds_the_gross_instead_of_stranding()
    {
        // THE STRANDING. One line listed at 9000; the shopper actually paid 7377 after a 2700 discount.
        // Deriving the refund from the list price asks Payments for 9000 — more than the captured
        // payment — and the old consumer just logged and returned: no RefundCompleted, no failure, the
        // RMA parked in RefundPending for good. The amount must be capped at the refundable gross.
        var orderId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        await fixture.SeedSucceededPaymentAsync(orderId, grossMinor: Gross, taxMinor: 578);
        await fixture.SeedOrderSnapshotAsync(orderId, Gross, "buyer@example.com",
            (productId, "Discounted widget", Subtotal, 1, Discount));

        using var customer = Customer();
        var response = await customer.PostAsJsonAsync("/rma", new { orderId, reason = "damaged" });
        response.EnsureSuccessStatusCode();
        var rmaId = (await response.Content.ReadFromJsonAsync<RmaCreated>())!.RmaId;
        await WaitForStateAsync(rmaId, "Requested");

        // Never more than the shopper actually paid.
        var amount = await SagaAmountAsync(rmaId);
        Assert.True(amount <= Gross, $"RMA amount {amount} exceeds the refundable gross {Gross}");

        using var admin = Admin();
        (await admin.PostAsJsonAsync($"/admin/rmas/{rmaId}/approve", new { requireReturn = false }))
            .EnsureSuccessStatusCode();

        // ...and it reaches a terminal state rather than stranding in RefundPending.
        await WaitForStateAsync(rmaId, "RefundIssued");
        Assert.Equal(0, await fixture.PaymentsTrialBalanceAsync());
    }

    [Fact]
    public async Task A_partial_return_refunds_the_discounted_line_value_not_the_list_price()
    {
        // THE OVER-REFUND. Two lines, 4000 + 5000 listed, 2700 taken off across them (1200/1500 by
        // value). Returning the 4000 line must give back 4000 − 1200 = 2800, not the 4000 the shopper
        // was never charged. 4000 slipped past Payments' remaining-balance check (4000 ≤ 7377), so the
        // loss was silent.
        var orderId = Guid.CreateVersion7();
        var small = Guid.CreateVersion7();
        var large = Guid.CreateVersion7();
        await fixture.SeedSucceededPaymentAsync(orderId, grossMinor: Gross, taxMinor: 578);
        await fixture.SeedOrderSnapshotAsync(orderId, Gross, "buyer@example.com",
            (small, "Small", 4_000, 1, 1_200),
            (large, "Large", 5_000, 1, 1_500));

        using var customer = Customer();
        var response = await customer.PostAsJsonAsync("/rma", new
        {
            orderId,
            reason = "one item damaged",
            lines = new[] { new { productId = small, quantity = 1 } },
        });
        response.EnsureSuccessStatusCode();
        var rmaId = (await response.Content.ReadFromJsonAsync<RmaCreated>())!.RmaId;
        await WaitForStateAsync(rmaId, "Requested");

        Assert.Equal(2_800, await SagaAmountAsync(rmaId));

        // What the customer is SHOWN for that line is what they get back (shown == refunded).
        var refundable = await customer.GetFromJsonAsync<RefundableOrderProbe>($"/orders/{orderId}/lines");
        var shown = Assert.Single(refundable!.Lines, l => l.ProductId == large); // small is now consumed
        Assert.Equal(3_500, shown.RefundableAmountMinor);
    }

    [Fact]
    public async Task Per_unit_discount_is_prorated_across_a_partially_returned_line()
    {
        // One line of 3 @ 1000 with 300 off the line. Returning 2 units gives back 2000 − 200 = 1800.
        var orderId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        await fixture.SeedSucceededPaymentAsync(orderId, grossMinor: 2_700, taxMinor: 0);
        await fixture.SeedOrderSnapshotAsync(orderId, 2_700, "buyer@example.com",
            (productId, "Three-pack", 1_000, 3, 300));

        using var customer = Customer();
        var response = await customer.PostAsJsonAsync("/rma", new
        {
            orderId,
            reason = "two damaged",
            lines = new[] { new { productId, quantity = 2 } },
        });
        response.EnsureSuccessStatusCode();
        var rmaId = (await response.Content.ReadFromJsonAsync<RmaCreated>())!.RmaId;
        await WaitForStateAsync(rmaId, "Requested");

        Assert.Equal(1_800, await SagaAmountAsync(rmaId));
    }

    [Fact]
    public async Task Pre_discount_snapshots_still_refund_but_never_above_the_captured_gross()
    {
        // BACK-COMPAT. An order confirmed before the contract carried DiscountMinor projects lines with
        // a 0 discount, so its refund basis is the list price — the historical behaviour. The gross cap
        // is what keeps that safe: the refund can never exceed what was actually captured.
        var orderId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        await fixture.SeedSucceededPaymentAsync(orderId, grossMinor: Gross, taxMinor: 0);
        // No discount on the line — exactly what an old projection looks like.
        await fixture.SeedOrderSnapshotAsync(orderId, Gross, "buyer@example.com",
            (productId, "Legacy line", Subtotal, 1, 0));

        using var customer = Customer();
        var response = await customer.PostAsJsonAsync("/rma", new { orderId, reason = "legacy" });
        response.EnsureSuccessStatusCode();
        var rmaId = (await response.Content.ReadFromJsonAsync<RmaCreated>())!.RmaId;
        await WaitForStateAsync(rmaId, "Requested");

        Assert.Equal(Gross, await SagaAmountAsync(rmaId));
    }

    [Fact]
    public async Task A_second_request_can_only_claim_what_is_left_of_the_gross()
    {
        // Two lines whose discounted value (1000 + 1000) exceeds the captured gross (1500) — a
        // shipping/tax shape the snapshot alone cannot see. The first RMA takes 1000; the second is
        // capped at the 500 that is left rather than over-refunding or stranding.
        var orderId = Guid.CreateVersion7();
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        await fixture.SeedSucceededPaymentAsync(orderId, grossMinor: 1_500, taxMinor: 0);
        await fixture.SeedOrderSnapshotAsync(orderId, 1_500, "buyer@example.com",
            (first, "First", 1_000, 1, 0),
            (second, "Second", 1_000, 1, 0));

        using var customer = Customer();
        var one = await customer.PostAsJsonAsync("/rma", new
        {
            orderId,
            reason = "first",
            lines = new[] { new { productId = first, quantity = 1 } },
        });
        one.EnsureSuccessStatusCode();
        Assert.Equal(1_000, await SagaAmountAsync((await one.Content.ReadFromJsonAsync<RmaCreated>())!.RmaId));

        var two = await customer.PostAsJsonAsync("/rma", new
        {
            orderId,
            reason = "second",
            lines = new[] { new { productId = second, quantity = 1 } },
        });
        two.EnsureSuccessStatusCode();
        Assert.Equal(500, await SagaAmountAsync((await two.Content.ReadFromJsonAsync<RmaCreated>())!.RmaId));

        // Nothing left at all → a 400, not a zero-value RMA.
        var three = await customer.PostAsJsonAsync("/rma", new { orderId, reason = "third" });
        Assert.Equal(HttpStatusCode.BadRequest, three.StatusCode);
    }

    [Fact]
    public async Task A_refund_the_payment_cannot_cover_fails_the_rma_instead_of_stranding()
    {
        // The last resort: the snapshot's gross and the captured payment disagree (a partially refunded
        // payment, a stale snapshot). Payments must SAY SO — RefundFailed → the RMA reaches a terminal
        // RefundFailed state and the shopper is notified — instead of logging and going quiet.
        var orderId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        await fixture.SeedSucceededPaymentAsync(orderId, grossMinor: 400, taxMinor: 0);
        await fixture.SeedOrderSnapshotAsync(orderId, 1_000, "buyer@example.com",
            (productId, "Over-valued", 1_000, 1, 0));

        using var customer = Customer();
        var response = await customer.PostAsJsonAsync("/rma", new { orderId, reason = "mismatch" });
        response.EnsureSuccessStatusCode();
        var rmaId = (await response.Content.ReadFromJsonAsync<RmaCreated>())!.RmaId;
        await WaitForStateAsync(rmaId, "Requested");

        using var admin = Admin();
        (await admin.PostAsJsonAsync($"/admin/rmas/{rmaId}/approve", new { requireReturn = false }))
            .EnsureSuccessStatusCode();

        await WaitForStateAsync(rmaId, "RefundFailed");
        Assert.Equal(0, await fixture.PaymentsTrialBalanceAsync()); // nothing was posted
    }

    private sealed record RefundableLineProbe(Guid ProductId, string Title, long UnitPriceMinor, int Quantity, long RefundableAmountMinor = 0);
    private sealed record RefundableOrderProbe(Guid OrderId, long GrossMinor, string Currency, List<RefundableLineProbe> Lines);

    private async Task<long> SagaAmountAsync(Guid rmaId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            using var scope = fixture.Support.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SupportDbContext>();
            var saga = await db.Rmas.AsNoTracking().FirstOrDefaultAsync(r => r.CorrelationId == rmaId);
            if (saga is not null)
            {
                return saga.AmountMinor;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"RMA {rmaId} never materialized.");
            }

            await Task.Delay(200);
        }
    }

    private async Task WaitForStateAsync(Guid rmaId, string expected)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        string? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Support.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SupportDbContext>();
            last = (await db.Rmas.AsNoTracking().FirstOrDefaultAsync(r => r.CorrelationId == rmaId))?.CurrentState;
            if (last == expected)
            {
                return;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"RMA {rmaId} never reached {expected} (last state: {last ?? "<none>"}).");
    }
}
