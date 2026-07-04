using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace ThreeCommerce.Admin.Services;

public static partial class LoginEndpoints
{
    public static void MapLoginEndpoints(this WebApplication app)
    {
        app.MapPost("/auth/login", async (HttpContext http, IHttpClientFactory factory) =>
        {
            var form = await http.Request.ReadFormAsync();
            var email = form["email"].ToString();
            var password = form["password"].ToString();
            var mfaCode = form["code"].ToString();

            var client = factory.CreateClient("gateway");
            var login = await client.PostAsJsonAsync("/api/identity/login", new { email, password });
            var token = SessionCookie().Match(login.Headers.TryGetValues("Set-Cookie", out var c) ? string.Join(";", c) : "").Groups[1].Value;
            if (!login.IsSuccessStatusCode || string.IsNullOrEmpty(token))
            {
                return Results.Redirect("/login?error=1");
            }

            // MFA-enrolled account: the session is pending until the challenge passes (mt6_10).
            var body = await login.Content.ReadFromJsonAsync<LoginBody>();
            if (body?.MfaRequired == true)
            {
                using var challenge = new HttpRequestMessage(HttpMethod.Post, "/api/identity/mfa/challenge")
                {
                    Content = JsonContent.Create(new { code = mfaCode }),
                };
                challenge.Headers.Add("Cookie", $"3c_session={token}");
                var challenged = string.IsNullOrWhiteSpace(mfaCode) ? null : await client.SendAsync(challenge);
                if (challenged is not { IsSuccessStatusCode: true })
                {
                    return Results.Redirect("/login?error=3"); // code missing or wrong
                }
            }

            // Verify admin role by probing an admin-only endpoint (introspection is internal-only).
            using var probe = new HttpRequestMessage(HttpMethod.Get, "/api/payments/admin/xero/sync-runs");
            probe.Headers.Add("Cookie", $"3c_session={token}");
            var probeResult = await client.SendAsync(probe);
            if (!probeResult.IsSuccessStatusCode)
            {
                return Results.Redirect("/login?error=2"); // not an admin
            }

            var claims = new List<Claim> { new(ClaimTypes.Name, email), new(ClaimTypes.Role, "admin"), new(GatewayClient.SessionClaim, token) };
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            return Results.Redirect("/");
        });

        app.MapPost("/auth/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        });
    }

    [GeneratedRegex("3c_session=([^;]+)")]
    private static partial Regex SessionCookie();

    private sealed record LoginBody(string? Message, bool MfaRequired);
}
