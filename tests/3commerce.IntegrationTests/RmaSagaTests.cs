using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Support.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// FR-9/FR-10: the RMA capstone. Customer requests a refund; admin approves; the saga
/// publishes the single Phase-3 RefundRequested contract; Payments refunds; RefundCompleted
/// advances the RMA to RefundIssued. Plus the double-approve no-op and deny path.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class RmaSagaTests(Phase4Fixture fixture)
{
    private sealed record RmaCreated(Guid RmaId);

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

    private async Task<Guid> RequestRmaAsync(Guid orderId, long amount)
    {
        using var customer = Customer();
        var response = await customer.PostAsJsonAsync("/rma", new
        {
            orderId,
            email = "buyer@example.com",
            amountMinor = amount,
            reason = "damaged",
        });
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<RmaCreated>();
        await WaitForStateAsync(created!.RmaId, "Requested");
        return created.RmaId;
    }

    [Fact]
    public async Task Approved_rma_drives_the_refund_and_reaches_RefundIssued()
    {
        var orderId = Guid.CreateVersion7();
        await fixture.SeedSucceededPaymentAsync(orderId, grossMinor: 11900, taxMinor: 1900);
        var rmaId = await RequestRmaAsync(orderId, 11900);

        using var admin = Admin();
        var approve = await admin.PostAsJsonAsync($"/admin/rmas/{rmaId}/approve", new { requireReturn = false });
        Assert.Equal(HttpStatusCode.Accepted, approve.StatusCode);

        await WaitForStateAsync(rmaId, "RefundIssued");
        Assert.Equal(0, await fixture.PaymentsTrialBalanceAsync()); // refund reversal balanced
    }

    [Fact]
    public async Task Double_approve_is_a_no_op()
    {
        var orderId = Guid.CreateVersion7();
        await fixture.SeedSucceededPaymentAsync(orderId, 5000, 0);
        var rmaId = await RequestRmaAsync(orderId, 5000);

        using var admin = Admin();
        var first = await admin.PostAsJsonAsync($"/admin/rmas/{rmaId}/approve", new { requireReturn = false });
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        // Once the saga has acted on the first approval (left Requested), a second approve
        // is rejected — the RMA is a no-op past Requested (FR-10).
        await WaitForStateAsync(rmaId, "RefundIssued");
        var second = await admin.PostAsJsonAsync($"/admin/rmas/{rmaId}/approve", new { requireReturn = false });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Denied_rma_does_not_refund()
    {
        var orderId = Guid.CreateVersion7();
        await fixture.SeedSucceededPaymentAsync(orderId, 5000, 0);
        var rmaId = await RequestRmaAsync(orderId, 5000);

        using var admin = Admin();
        var deny = await admin.PostAsync($"/admin/rmas/{rmaId}/deny", content: null);
        Assert.Equal(HttpStatusCode.Accepted, deny.StatusCode);
        await WaitForStateAsync(rmaId, "Denied");

        // Approving a denied RMA is rejected.
        var approve = await admin.PostAsJsonAsync($"/admin/rmas/{rmaId}/approve", new { requireReturn = false });
        Assert.Equal(HttpStatusCode.Conflict, approve.StatusCode);
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
