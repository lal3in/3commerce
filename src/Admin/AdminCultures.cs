using System.Globalization;

namespace ThreeCommerce.Admin;

/// <summary>
/// The cultures the admin UI can be displayed in. English is the base (the neutral <c>SharedResource.resx</c>);
/// every other culture is <b>discovered from the satellite assemblies the build emits</b> for its
/// <c>SharedResource.&lt;culture&gt;.resx</c>.
///
/// Adding a language is therefore a genuine drop-in: add <c>Resources/SharedResource.fr.resx</c>, rebuild — the
/// culture appears in <see cref="Supported"/>, in the request-localization options, and in the layout's language
/// switcher. No code change, no list to update. Untranslated keys fall back to English.
/// </summary>
public static class AdminCultures
{
    /// <summary>The base culture — the neutral .resx — and the culture an operator gets until they pick another.</summary>
    public const string Default = "en";

    /// <summary>English first, then every culture with a satellite resource assembly, ordered by name.</summary>
    public static IReadOnlyList<CultureInfo> Supported { get; } = Discover();

    private static List<CultureInfo> Discover()
    {
        var cultures = new List<CultureInfo> { CultureInfo.GetCultureInfo(Default) };
        var assembly = typeof(AdminCultures).Assembly;
        var satellite = $"{assembly.GetName().Name}.resources.dll";
        var baseDirectory = Path.GetDirectoryName(assembly.Location) is { Length: > 0 } location
            ? location
            : AppContext.BaseDirectory;

        if (!Directory.Exists(baseDirectory))
        {
            return cultures;
        }

        // The SDK emits satellite assemblies as <output>/<culture>/<assembly>.resources.dll — one folder per
        // translated culture. Their presence IS the list of shipped languages.
        foreach (var directory in Directory.EnumerateDirectories(baseDirectory).OrderBy(d => d, StringComparer.Ordinal))
        {
            if (!File.Exists(Path.Combine(directory, satellite)))
            {
                continue;
            }

            try
            {
                var culture = CultureInfo.GetCultureInfo(Path.GetFileName(directory));
                if (!cultures.Any(c => string.Equals(c.Name, culture.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    cultures.Add(culture);
                }
            }
            catch (CultureNotFoundException)
            {
                // A build-output folder that merely looks like a culture tag — ignore it.
            }
        }

        return cultures;
    }

    /// <summary>The name shown in the switcher — the language's own name ("English", "Deutsch"), title-cased.</summary>
    public static string DisplayName(CultureInfo culture)
    {
        var native = culture.NativeName;
        return native.Length == 0
            ? culture.Name
            : char.ToUpper(native[0], culture) + native[1..];
    }

    /// <summary>True when <paramref name="name"/> is a culture this build actually ships resources for.</summary>
    public static bool IsSupported(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && Supported.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}
