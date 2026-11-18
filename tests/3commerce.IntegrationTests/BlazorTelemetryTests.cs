using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using ThreeCommerce.BuildingBlocks.Observability.Blazor;
using ThreeCommerce.Identity.Api.Endpoints;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Unit lane (observability audit P1-4): the Blazor Admin / SupplierPortal telemetry names the tenant and user by
/// OPAQUE ids only — read from Identity's <c>GET /session</c> at sign-in, never the email/name — and the logging
/// error boundary hands the operator a trace reference.
/// </summary>
public sealed class BlazorTelemetryTests
{
    private static readonly Guid User = Guid.Parse("01930000-0000-7000-8000-0000000000b1");
    private static readonly Guid Tenant = Guid.Parse("00000000-0000-0000-0000-000000000001");

    [Fact]
    public void A_signed_in_operator_is_described_by_tenant_and_user_ids_never_the_email()
    {
        var principal = Principal(
            new Claim(ClaimTypes.Name, "ops@example.com"),
            new Claim(TelemetryClaims.UserId, User.ToString()),
            new Claim(TelemetryClaims.TenantId, Tenant.ToString()));

        var described = TelemetryCircuitHandler.Describe(principal);

        Assert.Equal(new CircuitPrincipal(Tenant.ToString(), User.ToString()), described);
        Assert.DoesNotContain("@", described.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Anonymous_and_malformed_principals_never_leak_a_claim_value()
    {
        Assert.Equal(new CircuitPrincipal("anonymous", "anonymous"), TelemetryCircuitHandler.Describe(null));
        Assert.Equal(new CircuitPrincipal("anonymous", "anonymous"), TelemetryCircuitHandler.Describe(new ClaimsPrincipal(new ClaimsIdentity())));

        // Signed in before the ids existed, or someone put an email where an id belongs: "unknown", not the value.
        var legacy = TelemetryCircuitHandler.Describe(Principal(new Claim(ClaimTypes.Name, "ops@example.com")));
        var wrong = TelemetryCircuitHandler.Describe(Principal(new Claim(TelemetryClaims.UserId, "ops@example.com")));
        Assert.Equal(new CircuitPrincipal("unknown", "unknown"), legacy);
        Assert.Equal(new CircuitPrincipal("unknown", "unknown"), wrong);
    }

    [Fact]
    public async Task Sign_in_reads_the_opaque_ids_from_identity_through_the_gateway()
    {
        HttpRequestMessage? seen = null;
        using var gateway = Gateway(request =>
        {
            seen = request;
            return Json($$"""{"userId":"{{User}}","tenantId":"{{Tenant}}"}""");
        });

        var claims = await TelemetryClaims.FetchAsync(gateway, "3c_session=abc");

        Assert.Equal("/api/identity/session", seen!.RequestUri!.AbsolutePath);
        Assert.Equal("3c_session=abc", Assert.Single(seen.Headers.GetValues("Cookie")));
        Assert.Equal(User.ToString(), Assert.Single(claims, c => c.Type == TelemetryClaims.UserId).Value);
        Assert.Equal(Tenant.ToString(), Assert.Single(claims, c => c.Type == TelemetryClaims.TenantId).Value);
    }

    [Fact]
    public async Task Sign_in_never_fails_because_the_ids_are_unavailable()
    {
        using var forbidden = Gateway(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var broken = Gateway(_ => Json("not json"));
        using var down = Gateway(_ => throw new HttpRequestException("connection refused"));

        Assert.Empty(await TelemetryClaims.FetchAsync(forbidden, "3c_session=abc"));
        Assert.Empty(await TelemetryClaims.FetchAsync(broken, "3c_session=abc"));
        Assert.Empty(await TelemetryClaims.FetchAsync(down, "3c_session=abc"));
    }

    [Fact]
    public void Identity_session_endpoint_returns_the_internal_claims_ids_for_any_role()
    {
        var supplier = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", User.ToString()), new Claim("tenant", Tenant.ToString()), new Claim("role", "supplier")], "internal"));

        var ok = Assert.IsType<Ok<SessionContextResponse>>(AuthEndpoints.GetSessionContext(supplier).Result);

        Assert.Equal(new SessionContextResponse(User, Tenant), ok.Value);
        Assert.IsType<UnauthorizedHttpResult>(AuthEndpoints.GetSessionContext(new ClaimsPrincipal(new ClaimsIdentity())).Result);
    }

    [Fact]
    public void The_error_reference_is_the_trace_id_when_there_is_one()
    {
        using var activity = new Activity("render").SetIdFormat(ActivityIdFormat.W3C).Start();

        Assert.Equal(activity.TraceId.ToHexString(), LoggingErrorBoundary.ReferenceFor(activity));
        Assert.Equal(12, LoggingErrorBoundary.ReferenceFor(null).Length);
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "Cookies"));

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static HttpClient Gateway(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new StubHandler(respond)) { BaseAddress = new Uri("http://gateway.test") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
