using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;

namespace ThreeCommerce.Admin.Services;

/// <summary>
/// The language switcher's POST target. The admin's UI language is a SESSION preference of the operator:
/// it lives in the standard ASP.NET culture cookie (<see cref="CookieRequestCultureProvider.DefaultCookieName"/>),
/// is read back by the request-localization middleware on every request, and is deliberately INDEPENDENT of
/// currency / tax / financial configuration (those belong to the storefront, not to the operator).
///
/// This is a plain form POST (not an interactive Blazor handler) because a SignalR circuit cannot write a
/// response cookie: the cookie has to be set on a real HTTP response, and the subsequent redirect gives the
/// new circuit a request that already carries the chosen culture.
/// </summary>
public static class CultureEndpoints
{
    public static void MapCultureEndpoints(this WebApplication app)
    {
        // [FromForm] opts this endpoint into antiforgery validation (app.UseAntiforgery), which the
        // <AntiforgeryToken /> in the switcher form satisfies.
        app.MapPost("/culture/set", ([FromForm] string culture, [FromForm] string? redirectUri, HttpContext http) =>
        {
            if (AdminCultures.IsSupported(culture))
            {
                http.Response.Cookies.Append(
                    CookieRequestCultureProvider.DefaultCookieName,
                    CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
                    new CookieOptions
                    {
                        // A year, so the operator's choice survives sign-out; HttpOnly is intentionally off —
                        // this is a display preference, not a credential, and nothing security-relevant reads it.
                        Expires = DateTimeOffset.UtcNow.AddYears(1),
                        IsEssential = true,
                        SameSite = SameSiteMode.Strict,
                        Path = "/",
                    });
            }

            // Only ever bounce back inside the admin — never to an attacker-supplied absolute URL.
            var target = redirectUri is not null && Uri.IsWellFormedUriString(redirectUri, UriKind.Relative) && redirectUri.StartsWith('/') && !redirectUri.StartsWith("//", StringComparison.Ordinal)
                ? redirectUri
                : "/";
            return Results.LocalRedirect(target);
        });
    }
}
