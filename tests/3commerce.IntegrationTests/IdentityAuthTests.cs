using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Identity;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Identity.Api.Endpoints;

namespace ThreeCommerce.IntegrationTests;

[Trait("Category", "Integration")]
[Collection(Phase2Collection.Name)]
public class IdentityAuthTests(Phase2Fixture fixture)
{
    private sealed record IntrospectResponse(Guid SessionId, Guid UserId, string Role, DateTimeOffset ExpiresAt);

    [Fact]
    public async Task Register_does_not_enumerate_existing_accounts()
    {
        using var identity = fixture.CreateIdentityFactory();
        using var client = identity.CreateClient();
        var email = $"enum-{Guid.NewGuid():N}@example.com";

        var first = await client.PostAsJsonAsync("/register", new { email, password = "a-strong-password" });
        var second = await client.PostAsJsonAsync("/register", new { email, password = "a-strong-password" });

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Session_is_revoked_immediately_on_logout()
    {
        using var identity = fixture.CreateIdentityFactory();
        using var client = identity.CreateClient(new() { HandleCookies = false });
        var email = $"logout-{Guid.NewGuid():N}@example.com";

        await client.PostAsJsonAsync("/register", new { email, password = "a-strong-password" });

        var login = await client.PostAsJsonAsync("/login", new { email, password = "a-strong-password" });
        login.EnsureSuccessStatusCode();
        var sessionToken = ExtractSessionCookie(login);

        // Active session introspects successfully.
        var before = await client.PostAsJsonAsync("/internal/introspection", new { token = sessionToken });
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        // Logout must carry the cookie back.
        using var logout = new HttpRequestMessage(HttpMethod.Post, "/logout");
        logout.Headers.Add("Cookie", $"{AuthEndpoints.SessionCookieName}={sessionToken}");
        (await client.SendAsync(logout)).EnsureSuccessStatusCode();

        // At the service level revocation is immediate (the ≤60s bound is the gateway cache).
        var after = await client.PostAsJsonAsync("/internal/introspection", new { token = sessionToken });
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Me_requires_valid_internal_claims()
    {
        using var identity = fixture.CreateIdentityFactory();
        using var client = identity.CreateClient(new() { HandleCookies = false });
        var email = $"me-{Guid.NewGuid():N}@example.com";

        await client.PostAsJsonAsync("/register", new { email, password = "a-strong-password" });
        var login = await client.PostAsJsonAsync("/login", new { email, password = "a-strong-password" });
        var sessionToken = ExtractSessionCookie(login);
        var session = await (await client.PostAsJsonAsync("/internal/introspection", new { token = sessionToken }))
            .Content.ReadFromJsonAsync<IntrospectResponse>();

        // No claims header -> 401.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me")).StatusCode);

        // Gateway-minted claims -> 200.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/me");
        request.Headers.Add(InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(session!.UserId, "customer"));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
    }

    private sealed record ProfileDto(Guid Id, string Email, string? Title, string? FirstName, string? MiddleName,
        string? LastName, string? PreferredName, string? Phone, DateOnly? DateOfBirth, bool MarketingConsent, bool EmailVerified, DateTimeOffset CreatedAt);

    [Fact]
    public async Task Register_persists_the_structured_member_profile_and_me_returns_it()
    {
        using var identity = fixture.CreateIdentityFactory();
        using var client = identity.CreateClient(new() { HandleCookies = false });
        var email = $"member-{Guid.NewGuid():N}@example.com";

        await client.PostAsJsonAsync("/register", new
        {
            email,
            password = "a-strong-password",
            title = "Ms",
            firstName = "Ada",
            middleName = "King",
            lastName = "Lovelace",
            preferredName = "Ada L.",
            phone = "+61400111222",
            dateOfBirth = "1990-05-01",
            marketingConsent = true,
        });

        var login = await client.PostAsJsonAsync("/login", new { email, password = "a-strong-password" });
        var session = await (await client.PostAsJsonAsync("/internal/introspection", new { token = ExtractSessionCookie(login) }))
            .Content.ReadFromJsonAsync<IntrospectResponse>();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/me");
        request.Headers.Add(InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(session!.UserId, "customer"));
        var profile = await (await client.SendAsync(request)).Content.ReadFromJsonAsync<ProfileDto>();

        Assert.Equal("Ms", profile!.Title);
        Assert.Equal("Ada", profile.FirstName);
        Assert.Equal("King", profile.MiddleName);
        Assert.Equal("Lovelace", profile.LastName);
        Assert.Equal("Ada L.", profile.PreferredName);
        Assert.Equal("+61400111222", profile.Phone);
        Assert.Equal(new DateOnly(1990, 5, 1), profile.DateOfBirth);
        Assert.True(profile.MarketingConsent);
    }

    [Fact]
    public async Task Wrong_password_does_not_log_in()
    {
        using var identity = fixture.CreateIdentityFactory();
        using var client = identity.CreateClient(new() { HandleCookies = false });
        var email = $"wrong-{Guid.NewGuid():N}@example.com";

        await client.PostAsJsonAsync("/register", new { email, password = "a-strong-password" });
        var login = await client.PostAsJsonAsync("/login", new { email, password = "WRONG" });

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Password_reset_changes_password_and_revokes_sessions()
    {
        using var identity = fixture.CreateIdentityFactory();
        var captured = new ConcurrentDictionary<string, string>();
        await using var listener = await StartTokenListenerAsync(captured);

        using var client = identity.CreateClient(new() { HandleCookies = false });
        var email = $"reset-{Guid.NewGuid():N}@example.com";

        await client.PostAsJsonAsync("/register", new { email, password = "original-password" });
        var login = await client.PostAsJsonAsync("/login", new { email, password = "original-password" });
        var oldSession = ExtractSessionCookie(login);

        await client.PostAsJsonAsync("/password-reset/request", new { email });
        var resetToken = await WaitForTokenAsync(captured, "reset:" + email);

        var confirm = await client.PostAsJsonAsync("/password-reset/confirm",
            new { token = resetToken, newPassword = "brand-new-password" });
        confirm.EnsureSuccessStatusCode();

        // Old session revoked; new password works; old password does not.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/internal/introspection", new { token = oldSession })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/login", new { email, password = "brand-new-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/login", new { email, password = "original-password" })).StatusCode);
    }

    private static string ExtractSessionCookie(HttpResponseMessage response)
    {
        var setCookie = response.Headers.GetValues("Set-Cookie")
            .First(c => c.StartsWith(AuthEndpoints.SessionCookieName, StringComparison.Ordinal));
        return setCookie.Split(';')[0].Split('=', 2)[1];
    }

    private async Task<IAsyncDisposable> StartTokenListenerAsync(ConcurrentDictionary<string, string> captured)
    {
        var bus = Bus.Factory.CreateUsingRabbitMq(cfg =>
        {
            cfg.Host(new Uri(fixture.RabbitMqUri));
            cfg.ReceiveEndpoint($"token-listener-{Guid.NewGuid():N}", e =>
            {
                e.Handler<UserRegistered>(ctx =>
                {
                    captured["verify:" + ctx.Message.Email] = ctx.Message.VerificationToken;
                    return Task.CompletedTask;
                });
                e.Handler<PasswordResetRequested>(ctx =>
                {
                    captured["reset:" + ctx.Message.Email] = ctx.Message.ResetToken;
                    return Task.CompletedTask;
                });
            });
        });
        await bus.StartAsync();
        return new BusHandle(bus);
    }

    private static async Task<string> WaitForTokenAsync(ConcurrentDictionary<string, string> captured, string key)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (captured.TryGetValue(key, out var token))
            {
                return token;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"No token captured for {key}.");
    }

    private sealed class BusHandle(IBusControl bus) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(bus.StopAsync());
    }
}
