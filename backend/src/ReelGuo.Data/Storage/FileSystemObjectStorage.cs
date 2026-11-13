namespace ReelGuo.Data.Storage;

/// <summary>
/// Stores objects as files under a root folder. Used for local development and tests (no Docker needed).
/// Cannot sign upload URLs, so uploads go through the API.
/// </summary>
public sealed class FileSystemObjectStorage : IObjectStorage
{
    private readonly string _root;

    public FileSystemObjectStorage(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    private string PathFor(string key)
    {
        StorageKeys.Require(key);
        var full = Path.GetFullPath(Path.Combine(_root, key));
        // Defence in depth: the key check already rules out traversal.
        if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Key escapes the storage root: '{key}'", nameof(key));
        return full;
    }

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".partial";
        await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            await content.CopyToAsync(file, cancellationToken);
        File.Move(temp, path, overwrite: true);
    }

    public async Task PutFileAsync(string key, string localPath, string contentType, CancellationToken cancellationToken)
    {
        await using var source = File.OpenRead(localPath);
        await PutAsync(key, source, contentType, cancellationToken);
    }

    public Task<StoredObject?> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return Task.FromResult<StoredObject?>(null);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return Task.FromResult<StoredObject?>(new StoredObject(stream, stream.Length, StorageKeys.ContentTypeFor(key)));
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken) => Task.FromResult(File.Exists(PathFor(key)));

    public async Task DownloadToFileAsync(string key, string localPath, CancellationToken cancellationToken)
    {
        await using var source = File.OpenRead(PathFor(key));
        await using var target = File.Create(localPath);
        await source.CopyToAsync(target, cancellationToken);
    }

    public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken)
    {
        var dir = Path.Combine(_root, prefix.TrimEnd('/'));
        if (!Directory.Exists(dir)) return Task.FromResult<IReadOnlyList<string>>([]);
        IReadOnlyList<string> keys = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".partial", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(_root, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(keys);
    }

    public Task DeletePrefixAsync(string prefix, CancellationToken cancellationToken)
    {
        var dir = Path.Combine(_root, prefix.TrimEnd('/'));
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        return Task.CompletedTask;
    }

    public Uri? CreateUploadUrl(string key, string contentType, TimeSpan validFor) => null;
}
