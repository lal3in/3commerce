using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;
using ThreeCommerce.BuildingBlocks.Infrastructure.Authz;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Unit lane (refund_permission_enforcement): the service-side PEP the refund endpoints use. It asks the Identity
/// PDP by the caller's <c>sub</c> + <c>tenant</c> claims (never trusting the admin role), answers 403 problem+json
/// with a Denied audit entry when the permission is missing, and fails closed (503, no audit) when the PDP can't
/// be reached. The end-to-end chain through the real PDP is <see cref="RefundPermissionTests"/>.
/// </summary>
public class PermissionGateTests
{
    private const string Refund = "payment.refund";
    private static readonly Guid Tenant = Guid.Parse("00000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task Allowed_decision_passes_and_asks_the_pdp_by_user_and_tenant()
    {
        var userId = Guid.CreateVersion7();
        var pdp = new StubPdp(HttpStatusCode.OK, Decision(Refund, allowed: true));
        var audit = new CapturingAudit();

        var denied = await Gate(pdp, audit).DenyUnlessAllowedAsync(
            Admin(userId), Refund, "Refund", "order-1", "payments.refund.request", NoDb(), default);

        Assert.Null(denied);
        Assert.Empty(audit.Drafts);
        var sent = Assert.Single(pdp.Requests);
        Assert.Equal(Guid.Empty, sent.PrincipalId);
        Assert.Equal(userId, sent.UserId);
        Assert.Equal(Tenant, sent.TenantId);
        Assert.Equal([Refund], sent.Actions);
    }

    [Fact]
    public async Task Denied_decision_is_a_403_problem_and_audited_as_denied()
    {
        var userId = Guid.CreateVersion7();
        var audit = new CapturingAudit();

        var denied = await Gate(new StubPdp(HttpStatusCode.OK, Decision(Refund, allowed: false)), audit)
            .DenyUnlessAllowedAsync(Admin(userId), Refund, "Refund", "order-1", "payments.refund.request", NoDb(), default);

        var problem = Assert.IsType<ProblemHttpResult>(denied);
        Assert.Equal(403, problem.StatusCode);
        Assert.Equal(PermissionGate.DeniedErrorCode, problem.ProblemDetails.Extensions["errorCode"]);
        Assert.Equal(Refund, problem.ProblemDetails.Extensions["permission"]);
        Assert.Contains(Refund, problem.ProblemDetails.Detail);

        var draft = Assert.Single(audit.Drafts);
        Assert.Equal(AuditOutcome.Denied, draft.Outcome);
        Assert.Equal("payments.refund.request", draft.Action);
        Assert.Equal("Refund", draft.ResourceType);
        Assert.Equal("order-1", draft.ResourceId);
        Assert.Equal(userId, draft.ActorId);
        Assert.Equal(Tenant, draft.TenantId);
    }

    [Fact]
    public async Task Admin_role_alone_is_not_enough_when_the_pdp_knows_no_principal()
    {
        // 404 from the PDP: no active principal behind this user in this tenant → nothing granted.
        var verdict = await Gate(new StubPdp(HttpStatusCode.NotFound, null), new CapturingAudit())
            .CheckAsync(Admin(Guid.CreateVersion7()), Refund, default);

        Assert.Equal(PermissionVerdict.Denied, verdict);
    }

    [Fact]
    public async Task A_decision_for_another_permission_never_counts()
    {
        var verdict = await Gate(new StubPdp(HttpStatusCode.OK, Decision("order.view", allowed: true)), new CapturingAudit())
            .CheckAsync(Admin(Guid.CreateVersion7()), Refund, default);

        Assert.Equal(PermissionVerdict.Denied, verdict);
    }

    [Fact]
    public async Task A_caller_without_a_sub_is_denied_without_asking_the_pdp()
    {
        var pdp = new StubPdp(HttpStatusCode.OK, Decision(Refund, allowed: true));
        var noSub = new ClaimsPrincipal(new ClaimsIdentity([new Claim("role", "admin"), new Claim("tenant", Tenant.ToString())], "test"));

        var verdict = await Gate(pdp, new CapturingAudit()).CheckAsync(noSub, Refund, default);

        Assert.Equal(PermissionVerdict.Denied, verdict);
        Assert.Empty(pdp.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task An_unavailable_pdp_fails_closed_with_a_503_and_no_audit(HttpStatusCode status)
    {
        var audit = new CapturingAudit();

        var denied = await Gate(new StubPdp(status, null), audit).DenyUnlessAllowedAsync(
            Admin(Guid.CreateVersion7()), Refund, "Rma", "rma-1", "support.rma.approve", NoDb(), default);

        var problem = Assert.IsType<ProblemHttpResult>(denied);
        Assert.Equal(503, problem.StatusCode);
        Assert.Equal(PermissionGate.UnavailableErrorCode, problem.ProblemDetails.Extensions["errorCode"]);
        Assert.Empty(audit.Drafts);
    }

    [Fact]
    public async Task An_unreachable_pdp_fails_closed()
    {
        var verdict = await Gate(new StubPdp(throws: new HttpRequestException("connection refused")), new CapturingAudit())
            .CheckAsync(Admin(Guid.CreateVersion7()), Refund, default);

        Assert.Equal(PermissionVerdict.Unavailable, verdict);
    }

    private static PermissionGate Gate(StubPdp pdp, CapturingAudit audit) =>
        new(new PolicyDecisionClient(new HttpClient(pdp) { BaseAddress = new Uri("http://identity.test") }),
            audit, new ConfigurationBuilder().Build(), NullLogger<PermissionGate>.Instance);

    private static ClaimsPrincipal Admin(Guid userId) => new(new ClaimsIdentity(
        [new Claim("sub", userId.ToString()), new Claim("role", "admin"), new Claim("tenant", Tenant.ToString())], "test"));

    private static PolicyDecisionResponse Decision(string key, bool allowed) => new(
        Guid.CreateVersion7(), IsPlatformAdmin: false,
        [new ActionDecision(key, allowed, PermissionRiskLevel.High, RequiresReason: false, RequiresApproval: allowed)], []);

    // Nothing is tracked, so SaveChanges returns without opening a connection.
    private static DbContext NoDb() => new(new DbContextOptionsBuilder().UseNpgsql("Host=localhost;Database=none").Options);

    private sealed class StubPdp(HttpStatusCode status = HttpStatusCode.OK, PolicyDecisionResponse? body = null, Exception? throws = null)
        : HttpMessageHandler
    {
        public List<PolicyDecisionRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("/internal/authz/decide", request.RequestUri!.AbsolutePath);
            Requests.Add((await request.Content!.ReadFromJsonAsync<PolicyDecisionRequest>(ct))!);
            if (throws is not null)
            {
                throw throws;
            }

            var response = new HttpResponseMessage(status);
            if (body is not null)
            {
                response.Content = JsonContent.Create(body, options: new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }

            return response;
        }
    }

    private sealed class CapturingAudit : IAuditRecorder
    {
        public List<AuditDraft> Drafts { get; } = [];

        public Task RecordAsync(AuditDraft draft, CancellationToken ct)
        {
            Drafts.Add(draft);
            return Task.CompletedTask;
        }
    }
}
