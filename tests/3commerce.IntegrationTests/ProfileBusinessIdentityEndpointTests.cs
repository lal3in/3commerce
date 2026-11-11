using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Identity.Api.Endpoints;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// ADR-0063 P3b — the member's saved default business identity over HTTP: <c>GET/PUT/DELETE /me/business-identity</c>
/// and its read-back on <c>GET /me</c>. Validated with checkout's per-scheme rules; a refusal is 400 problem+json
/// naming the field and saves nothing; the scheme crosses HTTP as a number; one member never sees another's.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase2Collection.Name)]
public class ProfileBusinessIdentityEndpointTests(Phase2Fixture fixture)
{
    private sealed record IntrospectResponse(Guid SessionId, Guid UserId, string Role, DateTimeOffset ExpiresAt);

    [Fact]
    public async Task A_member_saves_reads_and_clears_the_default_business_identity()
    {
        using var identity = fixture.CreateIdentityFactory();
        using var client = identity.CreateClient(new() { HandleCookies = false });
        var claims = await RegisterAsync(client);

        // Nothing saved yet: 204, and /me carries a null block.
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(client, HttpMethod.Get, "/me/business-identity", claims)).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await MeAsync(client, claims)).GetProperty("businessIdentity").ValueKind);

        var saved = await SendAsync(client, HttpMethod.Put, "/me/business-identity", claims,
            new { companyName = "  Acme GmbH ", taxId = "de 123 456 789", taxIdScheme = 5 });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var normalized = await saved.Content.ReadFromJsonAsync<JsonElement>();
        AssertAcme(normalized);

        // Read back on both surfaces, scheme as a NUMBER (platform invariant).
        AssertAcme(await (await SendAsync(client, HttpMethod.Get, "/me/business-identity", claims)).Content.ReadFromJsonAsync<JsonElement>());
        AssertAcme((await MeAsync(client, claims)).GetProperty("businessIdentity"));

        // Replace with a company that has no tax ID: the old number and scheme are gone.
        var replaced = await SendAsync(client, HttpMethod.Put, "/me/business-identity", claims, new { companyName = "Sole Trader Co" });
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var plain = (await MeAsync(client, claims)).GetProperty("businessIdentity");
        Assert.Equal("Sole Trader Co", plain.GetProperty("companyName").GetString());
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("taxId").ValueKind);
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("taxIdScheme").ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(client, HttpMethod.Delete, "/me/business-identity", claims)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(client, HttpMethod.Get, "/me/business-identity", claims)).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await MeAsync(client, claims)).GetProperty("businessIdentity").ValueKind);
    }

    [Theory]
    [InlineData("""{"companyName":""}""", "companyName")]
    [InlineData("""{"companyName":"Acme","taxId":"DE123456789"}""", "taxIdScheme")]
    [InlineData("""{"companyName":"Acme","taxIdScheme":5}""", "taxId")]
    [InlineData("""{"companyName":"Acme","taxId":"DE12345678","taxIdScheme":5}""", "taxId")]
    [InlineData("""{"companyName":"Acme","taxId":"SR-1234","taxIdScheme":10}""", "taxIdJurisdiction")]
    [InlineData("""{"companyName":"Acme","taxId":"DE123456789","taxIdScheme":42}""", "taxIdScheme")]
    public async Task An_invalid_identity_is_a_400_naming_the_field_and_saves_nothing(string body, string field)
    {
        using var identity = fixture.CreateIdentityFactory();
        using var client = identity.CreateClient(new() { HandleCookies = false });
        var claims = await RegisterAsync(client);
        (await SendAsync(client, HttpMethod.Put, "/me/business-identity", claims,
            new { companyName = "Acme GmbH", taxId = "DE123456789", taxIdScheme = 5 })).EnsureSuccessStatusCode();

        using var request = new HttpRequestMessage(HttpMethod.Put, "/me/business-identity")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(InternalClaimsAuth.HeaderName, claims);
        var refused = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("application/problem+json", refused.Content.Headers.ContentType?.MediaType);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(field, problem.GetProperty("field").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), $"errors has no '{field}' key: {problem}");

        // The previously saved identity is untouched.
        AssertAcme((await MeAsync(client, claims)).GetProperty("businessIdentity"));
    }

    [Fact]
    public async Task The_identity_needs_internal_claims_and_belongs_to_its_member_only()
    {
        using var identity = fixture.CreateIdentityFactory();
        using var client = identity.CreateClient(new() { HandleCookies = false });

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me/business-identity")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PutAsJsonAsync("/me/business-identity", new { companyName = "Anon Ltd" })).StatusCode);

        var owner = await RegisterAsync(client);
        var other = await RegisterAsync(client);
        (await SendAsync(client, HttpMethod.Put, "/me/business-identity", owner,
            new { companyName = "Acme GmbH", taxId = "DE123456789", taxIdScheme = 5 })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(client, HttpMethod.Get, "/me/business-identity", other)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(client, HttpMethod.Delete, "/me/business-identity", other)).StatusCode);
        AssertAcme((await MeAsync(client, owner)).GetProperty("businessIdentity"));
    }

    private static void AssertAcme(JsonElement identity)
    {
        Assert.Equal("Acme GmbH", identity.GetProperty("companyName").GetString());
        Assert.Equal("DE123456789", identity.GetProperty("taxId").GetString());
        Assert.Equal(JsonValueKind.Number, identity.GetProperty("taxIdScheme").ValueKind);
        Assert.Equal(5, identity.GetProperty("taxIdScheme").GetInt32());
        Assert.Equal("DE", identity.GetProperty("taxIdJurisdiction").GetString());
    }

    private async Task<string> RegisterAsync(HttpClient client)
    {
        var email = $"business-{Guid.NewGuid():N}@example.com";
        await client.PostAsJsonAsync("/register", new { email, password = "a-strong-password", firstName = "Ada", lastName = "Lovelace" });
        var login = await client.PostAsJsonAsync("/login", new { email, password = "a-strong-password" });
        var token = login.Headers.GetValues("Set-Cookie")
            .First(c => c.StartsWith(AuthEndpoints.SessionCookieName, StringComparison.Ordinal))
            .Split(';')[0].Split('=', 2)[1];
        var session = await (await client.PostAsJsonAsync("/internal/introspection", new { token }))
            .Content.ReadFromJsonAsync<IntrospectResponse>();
        return fixture.MintInternalClaims(session!.UserId, "customer");
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string claims, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(InternalClaimsAuth.HeaderName, claims);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> MeAsync(HttpClient client, string claims)
    {
        var response = await SendAsync(client, HttpMethod.Get, "/me", claims);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
