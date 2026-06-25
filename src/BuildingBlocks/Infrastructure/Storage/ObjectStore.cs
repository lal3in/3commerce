namespace ThreeCommerce.BuildingBlocks.Infrastructure.Storage;

/// <summary>
/// Object storage seam (mt6_9): owns bytes + access only — never domain meaning (that metadata lives in
/// the owning service). A local/dev adapter ships; production swaps in an S3/blob adapter behind this.
/// </summary>
public interface IObjectStore
{
    public Task PutAsync(string key, Stream content, string contentType, CancellationToken ct);

    public Task<Stream?> GetAsync(string key, CancellationToken ct);

    public Task<bool> ExistsAsync(string key, CancellationToken ct);

    public Task DeleteAsync(string key, CancellationToken ct);
}

/// <summary>
/// Builds tenant-scoped, traversal-safe object keys (mt6_9). A caller-supplied file name is reduced to a
/// bare, sanitized name so no path component can escape the tenant prefix.
/// </summary>
public static class StoredObjectKey
{
    public static string For(Guid tenantId, string category, string id, string fileName) =>
        $"{tenantId:N}/{Slug(category)}/{Slug(id)}/{SafeFileName(fileName)}";

    private static string Slug(string value)
    {
        var slug = new string(value.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return string.IsNullOrEmpty(slug) ? "x" : slug;
    }

    private static string SafeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName); // drops any directory / traversal segments
        var cleaned = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_').ToArray());
        return cleaned is "" or "." or ".." ? "file" : cleaned;
    }
}

/// <summary>
/// Upload validation (mt6_9 GOTCHA): an allow-list of safe image content types + a size cap. svg+xml,
/// html, and executables are rejected (active content / scripting). Stripping embedded metadata (EXIF)
/// is the image-processing step that runs after this gate.
/// </summary>
public static class UploadPolicy
{
    public const long MaxImageBytes = 10 * 1024 * 1024;

    private static readonly HashSet<string> AllowedImageTypes =
        new(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/webp", "image/gif" };

    public static bool ValidateImage(string contentType, long sizeBytes, out string? error)
    {
        error = null;
        if (sizeBytes <= 0)
        {
            error = "The file is empty.";
            return false;
        }

        if (sizeBytes > MaxImageBytes)
        {
            error = "The file exceeds the 10 MB image limit.";
            return false;
        }

        if (!AllowedImageTypes.Contains(contentType))
        {
            error = $"Unsupported image type '{contentType}'.";
            return false;
        }

        return true;
    }
}
