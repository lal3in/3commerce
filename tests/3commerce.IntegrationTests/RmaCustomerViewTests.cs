using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Support.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// The customer's view of their refund requests: requesting a partial refund records the line
/// quantities so the refundable list decrements (4 → 3) and the request shows up with its lifecycle
/// status — and a denied request releases the units back to refundable.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class RmaCustomerViewTests(Phase4Fixture fixture)
{
    private sealed record RmaCreated(Guid RmaId);
    private sealed record RefundableLine(Guid ProductId, string Title, long UnitPriceMinor, int Quantity);
    private sealed record RefundableOrder(Guid OrderId, long GrossMinor, string Currency, List<RefundableLine> Lines);
    private sealed record CustomerRmaLine(Guid ProductId, string Title, int Quantity, long UnitPriceMinor);
    private sealed record CustomerRma(Guid Id, long AmountMinor, string Currency, string Reason, string State, DateTimeOffset CreatedAt, List<CustomerRmaLine> Lines);

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

    private async Task<int> RefundableQtyAsync(HttpClient customer, Guid orderId, Guid productId)
    {
        var order = await customer.GetFromJsonAsync<RefundableOrder>($"/orders/{orderId}/lines");
        return order!.Lines.Single(l => l.ProductId == productId).Quantity;
    }

    [Fact]
    public async Task Partial_refund_decrements_the_refundable_quantity_and_lists_the_request()
    {
        var orderId = Guid.CreateVersion7();
        var widget = Guid.CreateVersion7();
        // One line: 4 × 1000.
        await fixture.SeedOrderSnapshotAsync(orderId, 4000, "buyer@example.com", (widget, "Widget", 1000, 4));
        using var customer = Customer();

        Assert.Equal(4, await RefundableQtyAsync(customer, orderId, widget)); // all 4 refundable up front

        // Request a refund for a single unit.
        var response = await customer.PostAsJsonAsync("/rma", new
        {
            orderId,
            reason = "one arrived scratched",
            lines = new[] { new { productId = widget, quantity = 1 } },
        });
        response.EnsureSuccessStatusCode();
        var rmaId = (await response.Content.ReadFromJsonAsync<RmaCreated>())!.RmaId;

        // Only 3 remain refundable (the bug: it used to still show 4).
        Assert.Equal(3, await RefundableQtyAsync(customer, orderId, widget));

        // The request shows in the customer's list with the line and its (initial) status.
        var rmas = await customer.GetFromJsonAsync<List<CustomerRma>>($"/tickets/rmas/by-order/{orderId}");
        var mine = Assert.Single(rmas!, r => r.Id == rmaId);
        Assert.Equal(1000, mine.AmountMinor);
        Assert.Equal("Requested", mine.State);
        var line = Assert.Single(mine.Lines);
        Assert.Equal(widget, line.ProductId);
        Assert.Equal(1, line.Quantity);
    }

    [Fact]
    public async Task Denied_request_releases_the_units_back_to_refundable()
    {
        var orderId = Guid.CreateVersion7();
        var widget = Guid.CreateVersion7();
        await fixture.SeedSucceededPaymentAsync(orderId, 4000, 0);
        await fixture.SeedOrderSnapshotAsync(orderId, 4000, "buyer@example.com", (widget, "Widget", 1000, 4));
        using var customer = Customer();

        var response = await customer.PostAsJsonAsync("/rma", new
        {
            orderId,
            reason = "changed my mind",
            lines = new[] { new { productId = widget, quantity = 2 } },
        });
        response.EnsureSuccessStatusCode();
        var rmaId = (await response.Content.ReadFromJsonAsync<RmaCreated>())!.RmaId;
        Assert.Equal(2, await RefundableQtyAsync(customer, orderId, widget)); // 2 held

        using var admin = Admin();
        await WaitForStateAsync(rmaId, "Requested");
        var deny = await admin.PostAsync($"/admin/rmas/{rmaId}/deny", content: null);
        deny.EnsureSuccessStatusCode();
        await WaitForStateAsync(rmaId, "Denied");

        // A denied request no longer consumes units — all 4 are refundable again.
        Assert.Equal(4, await RefundableQtyAsync(customer, orderId, widget));
    }

    private async Task WaitForStateAsync(Guid rmaId, string expected)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Support.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SupportDbContext>();
            var state = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .FirstOrDefaultAsync(db.Rmas.Where(r => r.CorrelationId == rmaId));
            if (state?.CurrentState == expected)
            {
                return;
            }

            await Task.Delay(300);
        }

        throw new TimeoutException($"RMA {rmaId} did not reach {expected}.");
    }
}
