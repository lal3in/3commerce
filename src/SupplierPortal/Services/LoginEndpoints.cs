using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using ThreeCommerce.BuildingBlocks.Observability.Blazor;

namespace ThreeCommerce.SupplierPortal.Services;

public static partial class LoginEndpoints
{
    public static void MapLoginEndpoints(this WebApplication app)
    {
        app.MapPost("/auth/login", async (HttpContext http, IHttpClientFactory factory) =>
        {
            var form = await http.Request.ReadFormAsync();
            var email = form["email"].ToString();
            var password = form["password"].ToString();

            var client = factory.CreateClient("gateway");
            var login = await client.PostAsJsonAsync("/api/identity/login", new { email, password });
            var token = SessionCookie().Match(login.Headers.TryGetValues("Set-Cookie", out var c) ? string.Join(";", c) : "").Groups[1].Value;
            if (!login.IsSuccessStatusCode || string.IsNullOrEmpty(token))
            {
                return Results.Redirect("/login?error=1");
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, email),
                new(SupplierGatewayClient.SessionClaim, token),
            };
            // Opaque user + tenant ids for the circuit telemetry (audit P1-4) — best effort, never blocks sign-in.
            claims.AddRange(await TelemetryClaims.FetchAsync(client, $"3c_session={token}", http.RequestAborted));
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
}
