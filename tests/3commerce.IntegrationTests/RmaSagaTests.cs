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
    private sealed record RmaListDto(
        Guid Id, Guid OrderId, string? Email, long AmountMinor, string? Reason, string State, DateTimeOffset CreatedAt,
        DateTimeOffset? ReturnReceivedAt, string? DispositionKind, string? StorageReason, string? DispositionComments);

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

    // Full-order refund: no line selection => the server uses the snapshot gross (BL-8).
    private async Task<Guid> RequestRmaAsync(Guid orderId, long amount)
    {
        await fixture.SeedOrderSnapshotAsync(orderId, amount);
        using var customer = Customer();
        var response = await customer.PostAsJsonAsync("/rma", new
        {
            orderId,
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
    public async Task Per_line_selection_refunds_only_the_chosen_lines_at_server_prices()
    {
        var orderId = Guid.CreateVersion7();
        var keyboard = Guid.CreateVersion7();
        var lamp = Guid.CreateVersion7();
        // Two lines: keyboard 2 × 5000, lamp 1 × 1900 => gross 11900.
        await fixture.SeedSucceededPaymentAsync(orderId, grossMinor: 11900, taxMinor: 0);
        await fixture.SeedOrderSnapshotAsync(orderId, 11900, "buyer@example.com",
            (keyboard, "Keyboard", 5000, 2),
            (lamp, "Lamp", 1900, 1));

        using var customer = Customer();
        // Refund one keyboard only. The client sends quantities, never an amount.
        var response = await customer.PostAsJsonAsync("/rma", new
        {
            orderId,
            reason = "one item damaged",
            lines = new[] { new { productId = keyboard, quantity = 1 } },
        });
        response.EnsureSuccessStatusCode();
        var rmaId = (await response.Content.ReadFromJsonAsync<RmaCreated>())!.RmaId;
        await WaitForStateAsync(rmaId, "Requested");

        // Server derived 1 × 5000 from the snapshot — not the order total.
        Assert.Equal(5000, await SagaAmountAsync(rmaId));

        using var admin = Admin();
        var approve = await admin.PostAsJsonAsync($"/admin/rmas/{rmaId}/approve", new { requireReturn = false });
        Assert.Equal(HttpStatusCode.Accepted, approve.StatusCode);
        await WaitForStateAsync(rmaId, "RefundIssued");
        Assert.Equal(0, await fixture.PaymentsTrialBalanceAsync());
    }

    [Fact]
    public async Task Received_return_is_dispositioned_to_storage_with_reason_then_edited_to_restock()
    {
        var orderId = Guid.CreateVersion7();
        await fixture.SeedSucceededPaymentAsync(orderId, 5000, 0);
        var rmaId = await RequestRmaAsync(orderId, 5000);
        using var admin = Admin();

        // Disposition before the return is received is rejected.
        var early = await admin.PostAsJsonAsync($"/admin/rmas/{rmaId}/disposition", new { kind = "Storage", storageReason = "Damage" });
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);

        // Approve (require return) → mark received → refund runs to completion.
        await admin.PostAsJsonAsync($"/admin/rmas/{rmaId}/approve", new { requireReturn = true });
        await WaitForStateAsync(rmaId, "AwaitingReturn");
        (await admin.PostAsync($"/admin/rmas/{rmaId}/return-received", content: null)).EnsureSuccessStatusCode();
        await WaitForStateAsync(rmaId, "RefundIssued");

        // Storage with a reason + comments.
        var store = await admin.PostAsJsonAsync($"/admin/rmas/{rmaId}/disposition",
            new { kind = "Storage", storageReason = "Damage", comments = "box crushed in transit" });
        Assert.Equal(HttpStatusCode.OK, store.StatusCode);

        var list = await admin.GetFromJsonAsync<List<RmaListDto>>("/admin/rmas");
        var row = Assert.Single(list!, r => r.Id == rmaId);
        Assert.NotNull(row.ReturnReceivedAt);
        Assert.Equal("Storage", row.DispositionKind);
        Assert.Equal("Damage", row.StorageReason);
        Assert.Equal("box crushed in transit", row.DispositionComments);

        // Storage without a reason is rejected.
        var noReason = await admin.PostAsJsonAsync($"/admin/rmas/{rmaId}/disposition", new { kind = "Storage" });
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        // Editable: switch the disposition to Restock — the reason clears.
        (await admin.PostAsJsonAsync($"/admin/rmas/{rmaId}/disposition", new { kind = "Restock" })).EnsureSuccessStatusCode();
        var after = await admin.GetFromJsonAsync<List<RmaListDto>>("/admin/rmas");
        var edited = Assert.Single(after!, r => r.Id == rmaId);
        Assert.Equal("Restock", edited.DispositionKind);
        Assert.Null(edited.StorageReason);
    }

    private async Task<long> SagaAmountAsync(Guid rmaId)
    {
        using var scope = fixture.Support.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SupportDbContext>();
        var saga = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstAsync(db.Rmas.Where(r => r.CorrelationId == rmaId));
        return saga.AmountMinor;
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
