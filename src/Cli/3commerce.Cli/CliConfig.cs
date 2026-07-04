using System.Text.Json;

namespace ThreeCommerce.Cli;

/// <summary>
/// Persisted CLI session (def_3 / mt1_6): the opaque gateway session token + context, stored at
/// ~/.3commerce/config.json with owner-only permissions. The token is the same 3c_session cookie a
/// browser would hold — the CLI talks to the Gateway exactly like any other client (never to
/// service databases).
/// </summary>
internal sealed record CliConfig(string Gateway, string? SessionToken, string? Email, DateTimeOffset? SavedAt)
{
    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".3commerce");
    private static string FilePath => Path.Combine(Dir, "config.json");

    public static CliConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<CliConfig>(File.ReadAllText(FilePath), Json) ?? Default;
            }
        }
        catch (JsonException)
        {
            // Corrupt config falls back to defaults; the next save rewrites it.
        }

        return Default;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public static void Delete()
    {
        if (File.Exists(FilePath))
        {
            File.Delete(FilePath);
        }
    }

    private static CliConfig Default => new("http://localhost:8080", null, null, null);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
}
