namespace ThreeCommerce.BuildingBlocks.Infrastructure.Storage;

/// <summary>
/// Filesystem-backed object store for local/dev (mt6_9). Keys map to files under a root; a resolved
/// path that would escape the root is refused (defence in depth on top of <see cref="StoredObjectKey"/>).
/// Production uses an S3/blob adapter behind <see cref="IObjectStore"/> instead.
/// </summary>
public sealed class LocalFileObjectStore(string rootPath) : IObjectStore
{
    private readonly string _root = Path.GetFullPath(rootPath);

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct)
    {
        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        await content.CopyToAsync(file, ct);
    }

    public Task<Stream?> GetAsync(string key, CancellationToken ct)
    {
        var path = Resolve(key);
        return Task.FromResult<Stream?>(File.Exists(path) ? File.OpenRead(path) : null);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct) => Task.FromResult(File.Exists(Resolve(key)));

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        var path = Resolve(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string Resolve(string key)
    {
        var full = Path.GetFullPath(Path.Combine(_root, key));
        if (full != _root && !full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The resolved object path escapes the storage root.");
        }

        return full;
    }
}
