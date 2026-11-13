using System.Text.RegularExpressions;

namespace ReelGuo.Data.Storage;

/// <summary>
/// One bucket of objects. Two buckets exist: <c>raw</c> (private originals and incoming imports)
/// and <c>media</c> (packaged HLS and public images, served through the CDN edge).
/// </summary>
public interface IObjectStorage
{
    Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken);
    Task PutFileAsync(string key, string localPath, string contentType, CancellationToken cancellationToken);

    /// <summary>Opens an object for reading, or returns null when it does not exist.</summary>
    Task<StoredObject?> OpenReadAsync(string key, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken);
    Task DownloadToFileAsync(string key, string localPath, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken);
    Task DeletePrefixAsync(string prefix, CancellationToken cancellationToken);

    /// <summary>
    /// A URL the browser can PUT the file to directly, or null when this backend cannot sign URLs
    /// (the filesystem backend); callers then fall back to the API's own upload endpoint.
    /// </summary>
    Uri? CreateUploadUrl(string key, string contentType, TimeSpan validFor);
}

public sealed record StoredObject(Stream Content, long Length, string? ContentType) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>The two buckets, injected together.</summary>
public sealed class StorageBuckets(IObjectStorage raw, IObjectStorage media)
{
    public IObjectStorage Raw { get; } = raw;
    public IObjectStorage Media { get; } = media;
}

public static partial class StorageKeys
{
    /// <summary>Keys are relative paths of safe characters only: no "..", no backslashes, no leading slash.</summary>
    public static bool IsValid(string? key) =>
        !string.IsNullOrEmpty(key)
        && key.Length <= 512
        && SafeKey().IsMatch(key)
        && !key.Split('/').Any(part => part is "" or "." or "..");

    public static string Require(string key) =>
        IsValid(key) ? key : throw new ArgumentException($"Invalid storage key: '{key}'", nameof(key));

    /// <summary>Where bulk-import files are placed in the raw bucket.</summary>
    public const string IncomingPrefix = "incoming/";

    /// <summary>Keeps a file name storage-safe: letters, digits, dot, dash, underscore.</summary>
    public static string SanitizeFileName(string name)
    {
        var baseName = Path.GetFileName(name.Replace('\\', '/'));
        var cleaned = UnsafeChars().Replace(baseName, "_").Trim('.', '_');
        return cleaned.Length == 0 ? "file" : cleaned[..Math.Min(cleaned.Length, 120)];
    }

    public static string ContentTypeFor(string key) => Path.GetExtension(key).ToLowerInvariant() switch
    {
        ".m3u8" => "application/vnd.apple.mpegurl",
        ".m4s" => "video/iso.segment",
        ".mp4" or ".m4v" => "video/mp4",
        ".ts" => "video/mp2t",
        ".mov" => "video/quicktime",
        ".mkv" => "video/x-matroska",
        ".webm" => "video/webm",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".vtt" => "text/vtt",
        ".json" => "application/json",
        ".csv" => "text/csv",
        _ => "application/octet-stream",
    };

    [GeneratedRegex("^[A-Za-z0-9._/-]+$")]
    private static partial Regex SafeKey();

    [GeneratedRegex("[^A-Za-z0-9._-]+")]
    private static partial Regex UnsafeChars();
}
