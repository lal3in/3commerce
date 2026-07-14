using System.Globalization;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Options;

namespace ThreeCommerce.SupplierPortal.Services;

/// <summary>
/// Session-language endpoint used by the layout's language switcher. Writes the ASP.NET Core
/// culture cookie and reloads the caller's page; the language is per supplier user session and is
/// independent of currency/financial configuration.
/// </summary>
public static class CultureEndpoints
{
    public static void MapCultureEndpoints(this WebApplication app)
    {
        // GET (not POST) so the switcher works on statically rendered pages such as /login without
        // an antiforgery token or interactivity. The endpoint only writes a preference cookie.
        app.MapGet("/culture/set", (string? culture, string? redirectUri, HttpContext http, IOptions<RequestLocalizationOptions> options) =>
        {
            var supported = options.Value.SupportedUICultures ?? [];
            var match = supported.FirstOrDefault(c => string.Equals(c.Name, culture, StringComparison.OrdinalIgnoreCase));

            // Unknown/unsupported culture: ignore it rather than persisting a value the app cannot serve.
            if (match is not null)
            {
                http.Response.Cookies.Append(
                    CookieRequestCultureProvider.DefaultCookieName,
                    CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(match, match)),
                    new CookieOptions
                    {
                        Path = "/",
                        HttpOnly = false,
                        IsEssential = true,
                        SameSite = SameSiteMode.Lax,
                        Expires = DateTimeOffset.UtcNow.AddYears(1),
                    });
            }

            var target = SafeLocalPath(redirectUri);
            return Results.LocalRedirect(target);
        });
    }

    private static string SafeLocalPath(string? redirectUri)
    {
        if (string.IsNullOrWhiteSpace(redirectUri) || !redirectUri.StartsWith('/') || redirectUri.StartsWith("//", StringComparison.Ordinal))
        {
            return "/";
        }

        return redirectUri;
    }

    /// <summary>Reads the supported cultures from configuration; English is the default.</summary>
    public static (string DefaultCulture, string[] SupportedCultures) ReadLocalizationConfig(IConfiguration configuration)
    {
        var supported = configuration.GetSection("Localization:SupportedCultures").Get<string[]>();
        if (supported is null || supported.Length == 0)
        {
            supported = ["en"];
        }

        var defaultCulture = configuration["Localization:DefaultCulture"];
        if (string.IsNullOrWhiteSpace(defaultCulture) || !supported.Contains(defaultCulture, StringComparer.OrdinalIgnoreCase))
        {
            defaultCulture = supported[0];
        }

        // Fail fast on a typo'd culture code rather than silently serving English.
        foreach (var culture in supported)
        {
            _ = CultureInfo.GetCultureInfo(culture);
        }

        return (defaultCulture, supported);
    }
}
