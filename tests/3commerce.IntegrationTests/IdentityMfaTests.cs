using System.Net;
using System.Net.Http.Json;
using ThreeCommerce.Identity.Api.Endpoints;
using ThreeCommerce.Identity.Domain;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// MFA enforcement (def_1 / mt6_10): an enrolled account's login is pending until the TOTP
/// challenge passes, and a pending session introspects to nothing — so it holds no claims
/// anywhere on the platform. Codes are computed with the same RFC 6238 domain implementation
/// the service verifies with (pinned to the RFC vectors in TotpTests).
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase2Collection.Name)]
public class IdentityMfaTests(Phase2Fixture fixture)
{
    private sealed record EnrollStart(string SecretBase32, string OtpauthUri);
    private sealed record RecoveryCodesBody(string Message, List<string> RecoveryCodes);
    private sealed record LoginBody(string Message, bool MfaRequired);
    private sealed record Status(bool Enrolled, bool Pending, bool Required);

    [Fact]
    public async Task Enrolled_login_is_pending_until_the_challenge_passes()
    {
        using var identity = fixture.CreateIdentityFactory();
        using var client = identity.CreateClient(new() { HandleCookies = false });
        var email = $"mfa-{Guid.NewGuid():N}@example.com";
        await client.PostAsJsonAsync("/register", new { email, password = "a-strong-password" });

        // First login: not enrolled yet -> fully authenticated immediately.
        var login = await client.PostAsJsonAsync("/login", new { email, password = "a-strong-password" });
        Assert.False((await login.Content.ReadFromJsonAsync<LoginBody>())!.MfaRequired);
        var token = ExtractSessionCookie(login);

        var status = await GetJsonAsync<Status>(client, "/mfa/status", token);
        Assert.False(status.Enrolled);

        // Enroll: begin -> confirm with a code computed from the returned secret.
        var begin = await PostJsonAsync(client, "/mfa/enroll/begin", null, token);
        var start = (await begin.Content.ReadFromJsonAsync<EnrollStart>())!;
        var confirm = await PostJsonAsync(client, "/mfa/enroll/confirm",
            new { code = Totp.Compute(start.SecretBase32, DateTimeOffset.UtcNow) }, token);
        var recovery = (await confirm.Content.ReadFromJsonAsync<RecoveryCodesBody>())!;
        Assert.Equal(8, recovery.RecoveryCodes.Count);

        // The enrolling session itself stays fully authenticated.
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/internal/introspection", new { token })).StatusCode);

        // Second login: pending — flagged in the response AND invisible to introspection,
        // so the gateway mints no claims for it.
        var second = await client.PostAsJsonAsync("/login", new { email, password = "a-strong-password" });
        Assert.True((await second.Content.ReadFromJsonAsync<LoginBody>())!.MfaRequired);
        var pendingToken = ExtractSessionCookie(second);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/internal/introspection", new { token = pendingToken })).StatusCode);

        // Wrong code -> still pending; right code -> live session.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await PostJsonAsync(client, "/mfa/challenge", new { code = "000000" }, pendingToken)).StatusCode);
        var challenge = await PostJsonAsync(client, "/mfa/challenge",
            new { code = Totp.Compute(start.SecretBase32, DateTimeOffset.UtcNow) }, pendingToken);
        Assert.Equal(HttpStatusCode.OK, challenge.StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/internal/introspection", new { token = pendingToken })).StatusCode);
    }

    [Fact]
    public async Task Recovery_codes_complete_a_challenge_exactly_once()
    {
        using var identity = fixture.CreateIdentityFactory();
        using var client = identity.CreateClient(new() { HandleCookies = false });
        var email = $"mfa-rec-{Guid.NewGuid():N}@example.com";
        await client.PostAsJsonAsync("/register", new { email, password = "a-strong-password" });

        var login = await client.PostAsJsonAsync("/login", new { email, password = "a-strong-password" });
        var token = ExtractSessionCookie(login);
        var begin = await PostJsonAsync(client, "/mfa/enroll/begin", null, token);
        var start = (await begin.Content.ReadFromJsonAsync<EnrollStart>())!;
        var confirm = await PostJsonAsync(client, "/mfa/enroll/confirm",
            new { code = Totp.Compute(start.SecretBase32, DateTimeOffset.UtcNow) }, token);
        var recovery = (await confirm.Content.ReadFromJsonAsync<RecoveryCodesBody>())!;

        // Burn a recovery code on a pending login.
        var second = await client.PostAsJsonAsync("/login", new { email, password = "a-strong-password" });
        var pending = ExtractSessionCookie(second);
        Assert.Equal(HttpStatusCode.OK,
            (await PostJsonAsync(client, "/mfa/challenge", new { code = recovery.RecoveryCodes[0] }, pending)).StatusCode);

        // The same code is spent; the TOTP still works.
        var third = await client.PostAsJsonAsync("/login", new { email, password = "a-strong-password" });
        var pending2 = ExtractSessionCookie(third);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await PostJsonAsync(client, "/mfa/challenge", new { code = recovery.RecoveryCodes[0] }, pending2)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await PostJsonAsync(client, "/mfa/challenge",
                new { code = Totp.Compute(start.SecretBase32, DateTimeOffset.UtcNow) }, pending2)).StatusCode);
    }

    private static async Task<T> GetJsonAsync<T>(HttpClient client, string path, string sessionToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"{AuthEndpoints.SessionCookieName}={sessionToken}");
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string path, object? body, string sessionToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = body is null ? JsonContent.Create(new { }) : JsonContent.Create(body),
        };
        request.Headers.Add("Cookie", $"{AuthEndpoints.SessionCookieName}={sessionToken}");
        return await client.SendAsync(request);
    }

    private static string ExtractSessionCookie(HttpResponseMessage response)
    {
        var setCookie = string.Join(";", response.Headers.GetValues("Set-Cookie"));
        var start = setCookie.IndexOf("3c_session=", StringComparison.Ordinal) + "3c_session=".Length;
        return setCookie[start..setCookie.IndexOf(';', start)];
    }
}
