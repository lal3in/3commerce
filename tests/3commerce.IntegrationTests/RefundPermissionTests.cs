using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Support.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// refund_permission_enforcement (owner decision 2026-11-04): every refund-issuing admin endpoint — Payments
/// <c>POST /admin/refunds</c> (by order and by renewal / usage <c>chargeReference</c>), Support's instant refund
/// <c>POST /admin/rmas</c> and RMA approval — requires <c>payment.refund</c>, decided by the real Identity PDP
/// (in-process) from the caller's sub + tenant. Every caller here carries the <c>admin</c> role claim, so the role
/// alone is proven insufficient. Built-in defaults (owner decisions 2026-11-04 / 2026-11-05): Administrator, Finance and
/// Support refund; Ops and Merchandiser are refused. Negative controls: a refused key is not burnt, a return REQUEST
/// moves no money and is allowed, and the bootstrap admin keeps today's behaviour.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class RefundPermissionTests(Phase4Fixture fixture)
{
    private const string Refund = "payment.refund";

    private sealed record AuditEntryDto(
        Guid Id, long Sequence, DateTimeOffset OccurredAt, Guid? ActorId, string? ActorRole,
        string Action, string ResourceType, string ResourceId, string Outcome, string? Summary);

    private sealed record RmaDto(Guid Id, Guid OrderId, string State);

    private sealed record MyPermissions(bool IsPlatformAdmin, List<string> Permissions);

    private HttpClient Payments(Guid userId) => With(fixture.Payments.CreateClient(), fixture.MintInternalClaims(userId, "admin"));

    private HttpClient Support(Guid userId) => With(fixture.Support.CreateClient(), fixture.MintInternalClaims(userId, "admin"));

    private static HttpClient With(HttpClient client, string claims)
    {
        client.DefaultRequestHeaders.Add(InternalClaimsAuth.HeaderName, claims);
        return client;
    }

    private static HttpRequestMessage RefundRequest(string key, Guid orderId, long amount, string? chargeReference = null) =>
        new(HttpMethod.Post, "/admin/refunds")
        {
            Content = JsonContent.Create(new { orderId, amountMinor = amount, reason = "permission test", chargeReference }),
            Headers = { { "Idempotency-Key", key } },
        };

    [Theory]
    [InlineData("finance")]
    [InlineData("support")]
    public async Task Refund_roles_refund_by_order_and_by_charge_reference(string roleKey)
    {
        var user = await PdpTestSupport.SeedStaffUserAsync(fixture.Identity, roleKey);
        using var payments = Payments(user);

        var byOrder = await payments.SendAsync(RefundRequest($"perm-{Guid.NewGuid():N}", Guid.CreateVersion7(), 500));
        Assert.Equal(HttpStatusCode.Accepted, byOrder.StatusCode);

        var byCharge = await payments.SendAsync(RefundRequest($"perm-{Guid.NewGuid():N}", Guid.Empty, 500, $"renew-{Guid.NewGuid():N}:2"));
        Assert.Equal(HttpStatusCode.Accepted, byCharge.StatusCode);
    }

    [Theory]
    [InlineData("ops")]
    [InlineData("merchandiser")]
    public async Task A_role_without_refunds_and_the_admin_claim_is_refused_with_a_403_problem_and_a_denied_audit(string roleKey)
    {
        var denier = await PdpTestSupport.SeedStaffUserAsync(fixture.Identity, roleKey);
        var finance = await PdpTestSupport.SeedStaffUserAsync(fixture.Identity, "finance");
        var orderId = Guid.CreateVersion7();
        var key = $"perm-{Guid.NewGuid():N}";

        using (var denied = Payments(denier))
        {
            var response = await denied.SendAsync(RefundRequest(key, orderId, 700));
            await AssertPermissionProblemAsync(response);

            var byCharge = await denied.SendAsync(RefundRequest($"perm-{Guid.NewGuid():N}", Guid.Empty, 700, $"usage-{Guid.NewGuid():N}"));
            await AssertPermissionProblemAsync(byCharge);
        }

        var entry = await WaitForDeniedAsync("payments.refund.request", orderId.ToString(), denier);
        Assert.Equal("Refund", entry.ResourceType);
        Assert.Equal("admin", entry.ActorRole);
        Assert.Contains(Refund, entry.Summary);

        // Negative control: the refusal stored nothing under the key — the same key and body now succeed for Finance.
        using var allowed = Payments(finance);
        Assert.Equal(HttpStatusCode.Accepted, (await allowed.SendAsync(RefundRequest(key, orderId, 700))).StatusCode);
    }

    [Fact]
    public async Task The_admin_role_claim_alone_never_refunds()
    {
        // A staff user with no RBAC role, and a sub Identity has never heard of — both carry role=admin.
        var noRole = await PdpTestSupport.SeedStaffUserAsync(fixture.Identity, roleKey: null);
        foreach (var userId in new[] { noRole, Guid.CreateVersion7() })
        {
            using var payments = Payments(userId);
            await AssertPermissionProblemAsync(
                await payments.SendAsync(RefundRequest($"perm-{Guid.NewGuid():N}", Guid.CreateVersion7(), 100)));
        }
    }

    [Fact]
    public async Task The_bootstrap_admin_keeps_refunding()
    {
        using var payments = With(fixture.Payments.CreateClient(), fixture.RefundAdminClaims());
        var response = await payments.SendAsync(RefundRequest($"perm-{Guid.NewGuid():N}", Guid.CreateVersion7(), 100));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Rma_refund_and_approval_need_the_permission_but_a_return_request_does_not()
    {
        var ops = await PdpTestSupport.SeedStaffUserAsync(fixture.Identity, "ops");
        var support = await PdpTestSupport.SeedStaffUserAsync(fixture.Identity, "support");
        var orderId = Guid.CreateVersion7();
        await fixture.SeedSucceededPaymentAsync(orderId, grossMinor: 5000, taxMinor: 0);
        await fixture.SeedOrderSnapshotAsync(orderId, 5000);

        using var opsClient = Support(ops);

        // Instant (auto-approved) refund by Ops: refused, audited against the order.
        var instant = await opsClient.PostAsJsonAsync("/admin/rmas", new { orderId, reason = "refund please" });
        await AssertPermissionProblemAsync(instant);
        await WaitForDeniedAsync("support.rma.admin_refund", orderId.ToString(), ops);

        // Negative control: a return REQUEST moves no money, so even Ops may open it.
        var request = await opsClient.PostAsJsonAsync("/admin/rmas", new { orderId, reason = "return", autoApprove = false });
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
        var rmaId = (await request.Content.ReadFromJsonAsync<RmaDto>())!.Id;
        await WaitForStateAsync(rmaId, "Requested");

        // Approving ends in a refund: Ops is refused …
        await AssertPermissionProblemAsync(
            await opsClient.PostAsJsonAsync($"/admin/rmas/{rmaId}/approve", new { requireReturn = false }));
        await WaitForDeniedAsync("support.rma.approve", rmaId.ToString(), ops);
        await WaitForStateAsync(rmaId, "Requested"); // nothing moved

        // … Support (refunds by default, owner decision 2026-11-05) approves and the refund is issued.
        using var supportClient = Support(support);
        Assert.Equal(HttpStatusCode.Accepted,
            (await supportClient.PostAsJsonAsync($"/admin/rmas/{rmaId}/approve", new { requireReturn = false })).StatusCode);
        await WaitForStateAsync(rmaId, "RefundIssued");
        Assert.Equal(0, await fixture.PaymentsTrialBalanceAsync());
    }

    [Fact]
    public async Task Identity_reports_effective_permissions_for_the_admin_ui()
    {
        var finance = await PdpTestSupport.SeedStaffUserAsync(fixture.Identity, "finance");
        var support = await PdpTestSupport.SeedStaffUserAsync(fixture.Identity, "support");
        var ops = await PdpTestSupport.SeedStaffUserAsync(fixture.Identity, "ops");

        Assert.Contains(Refund, (await PermissionsOf(finance)).Permissions);
        Assert.Contains(Refund, (await PermissionsOf(support)).Permissions);
        Assert.Contains("rma.manage", (await PermissionsOf(support)).Permissions);
        Assert.DoesNotContain(Refund, (await PermissionsOf(ops)).Permissions);

        var unknown = await PermissionsOf(Guid.CreateVersion7());
        Assert.False(unknown.IsPlatformAdmin);
        Assert.Empty(unknown.Permissions);
    }

    private async Task<MyPermissions> PermissionsOf(Guid userId)
    {
        using var identity = With(fixture.Identity.CreateClient(), fixture.MintInternalClaims(userId, "admin"));
        return (await identity.GetFromJsonAsync<MyPermissions>("/me/permissions"))!;
    }

    private static async Task AssertPermissionProblemAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"expected 403, got {(int)response.StatusCode}: {raw}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(403, body.GetProperty("status").GetInt32());
        Assert.Equal("permission_denied", body.GetProperty("errorCode").GetString());
        Assert.Equal(Refund, body.GetProperty("permission").GetString());
        Assert.True(body.TryGetProperty("traceId", out _), "problem has no traceId");
    }

    private async Task<AuditEntryDto> WaitForDeniedAsync(string action, string resourceId, Guid actor)
    {
        using var audit = With(fixture.Audit.CreateClient(), fixture.MintInternalClaims(Guid.CreateVersion7(), "admin"));
        AuditEntryDto? found = null;
        await Eventually.UntilAsync(
            async () =>
            {
                var entries = await audit.GetFromJsonAsync<List<AuditEntryDto>>(
                    $"/admin/audit?tenantId={PdpTestSupport.DefaultTenant}&action={action}&resourceId={resourceId}&outcome=Denied");
                found = entries?.FirstOrDefault(e => e.ActorId == actor);
                return found is not null;
            },
            () => new TimeoutException($"No Denied '{action}' audit entry for {resourceId} by {actor}."),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromMilliseconds(250));
        return found!;
    }

    private async Task WaitForStateAsync(Guid rmaId, string expected)
    {
        string? last = null;
        await Eventually.UntilAsync(
            async () =>
            {
                using var scope = fixture.Support.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SupportDbContext>();
                last = (await db.Rmas.AsNoTracking().FirstOrDefaultAsync(r => r.CorrelationId == rmaId))?.CurrentState;
                return last == expected;
            },
            () => new TimeoutException($"RMA {rmaId} did not reach {expected} (last: {last ?? "none"})."),
            interval: TimeSpan.FromMilliseconds(300));
    }
}
