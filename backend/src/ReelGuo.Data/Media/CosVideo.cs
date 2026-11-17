using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ReelGuo.Data.Media;

/// <summary>
/// Where episode videos are kept. "Hls" (the default): the upload goes to the raw bucket and the worker packages
/// adaptive HLS into the media bucket. "Cos": Tencent COS holds the uploaded MP4 itself, as on the Viora (ReelPico)
/// platform. The admin's browser PUTs it straight to the bucket and viewers play it through short-lived signed links.
/// </summary>
public sealed class VideoOptions
{
    public const string Section = "Video";

    public string Provider { get; set; } = "Hls";
    public CosOptions Cos { get; set; } = new();

    public bool UsesCos => string.Equals(Provider, "Cos", StringComparison.OrdinalIgnoreCase);
}

public sealed class CosOptions
{
    /// <summary>A CAM sub-account's key, limited to this one bucket.</summary>
    public string SecretId { get; set; } = "";
    public string SecretKey { get; set; } = "";
    /// <summary>The full bucket name including the APPID, e.g. "reelguo-1300000000".</summary>
    public string Bucket { get; set; } = "";
    public string Region { get; set; } = "ap-singapore";
    /// <summary>A CDN domain bound to the bucket that forwards the query string. Empty serves from the bucket itself.</summary>
    public string CdnHost { get; set; } = "";
    /// <summary>The storage folder under videos/ that the per-episode upload uses; the library upload lets the admin pick.</summary>
    public string Folder { get; set; } = "reelguo";
    public int UploadUrlMinutes { get; set; } = 360;
    /// <summary>
    /// Replaces "https://{bucket}.cos.{region}.myqcloud.com" in every link: only for tests and local emulators.
    /// Leave empty for Tencent COS.
    /// </summary>
    public string Endpoint { get; set; } = "";
    /// <summary>How long a viewer's playback link works. Short, because a COS link cannot be revoked once handed out.</summary>
    public int PlaybackUrlMinutes { get; set; } = 30;
}

/// <summary>Object keys in the bucket, laid out as the Viora platform lays them out.</summary>
public static partial class CosKeys
{
    /// <summary>Every video lives under this prefix: "videos/{storage folder}/{title folder}/{file}".</summary>
    public const string VideoRoot = "videos/";

    /// <summary>
    /// True for a key in the COS bucket. Raw-bucket keys start with "uploads/" or "incoming/", never "videos/", so the
    /// prefix says where an episode's source lives.
    /// </summary>
    public static bool IsCosKey(string? key) => key is not null && key.StartsWith(VideoRoot, StringComparison.Ordinal);

    /// <summary>One part of a key: no slashes or control characters, no leading or trailing dots or spaces.</summary>
    public static string Segment(string s)
    {
        var clean = new string(s.Where(c => !char.IsControl(c) && c is not ('/' or '\\')).ToArray()).Trim().Trim('.').Trim();
        return clean.Length == 0 ? "_" : clean.Length > 200 ? clean[..200].TrimEnd() : clean;
    }

    /// <summary>
    /// "videos/{storage folder}/{title folder}/{file}". The original file name is kept, so an object in the bucket leads
    /// straight back to the file on the PC. <paramref name="suffix"/> goes before the extension ("name~suffix.mp4") when
    /// another episode already uses the plain name.
    /// </summary>
    public static string EpisodeKey(string folder, string titleFolder, string fileName, string? suffix = null)
    {
        var file = Segment(fileName);
        if (suffix is not null)
        {
            var ext = Path.GetExtension(file);
            file = $"{file[..^ext.Length]}~{suffix}{ext}";
        }
        return $"{VideoRoot}{Segment(folder)}/{Segment(titleFolder)}/{file}";
    }

    /// <summary>A storage folder name an admin typed: one segment, at most 64 characters, or null when it is unusable.</summary>
    public static string? FolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var s = Segment(name);
        return s == "_" || s.Length > 64 ? null : s;
    }

    /// <summary>
    /// The name key titles are matched by: Unicode NFKC, lower case, a leading "(Dubbed)" removed, and only letters and
    /// digits kept, in any script. "8-POUND BEEF，I'M DONE" and "8-Pound Beef, I'm Done" give the same key.
    /// </summary>
    public static string NameKey(string name)
    {
        var s = DubbedPrefix().Replace(name.Normalize(NormalizationForm.FormKC).Trim(), "");
        var sb = new StringBuilder(s.Length);
        foreach (var rune in s.ToLowerInvariant().EnumerateRunes())
            if (Rune.IsLetterOrDigit(rune)) sb.Append(rune.ToString());
        return sb.ToString();
    }

    /// <summary>The episode number in "Title_12.mp4" (or a downloader's "Title_12-da6a970674f2.mp4"), or null.</summary>
    public static int? EpisodeNumber(string fileName) =>
        EpisodePattern().Match(fileName) is { Success: true } m && int.TryParse(m.Groups[1].ValueSpan, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;

    /// <summary>A stable id for a title that came without a details file, from its name key.</summary>
    public static string DerivedId(string nameKey) =>
        "n-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(nameKey)))[..16];

    [GeneratedRegex(@"^\(\s*dubbed\s*\)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex DubbedPrefix();

    [GeneratedRegex(@"_(\d{1,4})(?:-[0-9a-f]{6,})?\.mp4", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodePattern();
}

/// <param name="Token">Session token for temporary credentials; null for a sub-account's permanent key.</param>
public sealed record CosCredentials(string SecretId, string SecretKey, string? Token = null);

/// <summary>
/// Tencent COS request signature (v5) as a query string, so a browser or a player can make one authorised request
/// without holding any credentials. The same signer the Viora platform uses.
///
///   SignKey      = hex(hmac_sha1(secretKey, keyTime))
///   HttpString   = "{method}\n{path}\n{params}\n{headers}\n"
///   StringToSign = "sha1\n{keyTime}\n{sha1(HttpString)}\n"
///   Signature    = hex(hmac_sha1(SignKey, StringToSign))       (keyed by SignKey as text, not its raw bytes)
///
/// The signature covers the method, the decoded object path, any query parameters and headers given, and the validity
/// window. https://www.tencentcloud.com/document/product/436/7778
/// </summary>
public static class CosSigner
{
    /// <param name="method">"put", "get", "head" or "delete": part of the signature, so a read link cannot write.</param>
    /// <param name="path">The object key with a leading slash, as the key really is (spaces, punctuation, Chinese).</param>
    public static string Presign(string method, string host, string path, CosCredentials cert,
        DateTimeOffset from, DateTimeOffset to, IReadOnlyDictionary<string, string>? parameters = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var keyTime = $"{from.ToUnixTimeSeconds()};{to.ToUnixTimeSeconds()}";
        var signKey = Hex(HMACSHA1.HashData(Encoding.UTF8.GetBytes(cert.SecretKey), Encoding.UTF8.GetBytes(keyTime)));
        var signed = Canonical(parameters);
        var paramString = string.Join('&', signed.Select(kv => $"{kv.Key}={kv.Value}"));
        var signedHeaders = Canonical(headers);
        var headerString = string.Join('&', signedHeaders.Select(kv => $"{kv.Key}={kv.Value}"));
        var httpString = $"{method.ToLowerInvariant()}\n{path}\n{paramString}\n{headerString}\n";
        var stringToSign = $"sha1\n{keyTime}\n{Hex(SHA1.HashData(Encoding.UTF8.GetBytes(httpString)))}\n";
        var signature = Hex(HMACSHA1.HashData(Encoding.UTF8.GetBytes(signKey), Encoding.UTF8.GetBytes(stringToSign)));

        var query = $"q-sign-algorithm=sha1&q-ak={Uri.EscapeDataString(cert.SecretId)}&q-sign-time={keyTime}" +
                    $"&q-key-time={keyTime}&q-header-list={string.Join(';', signedHeaders.Select(kv => kv.Key))}" +
                    $"&q-url-param-list={string.Join(';', signed.Select(kv => kv.Key))}&q-signature={signature}";
        if (!string.IsNullOrEmpty(cert.Token)) query += $"&x-cos-security-token={Uri.EscapeDataString(cert.Token)}";
        if (paramString.Length > 0) query = paramString + "&" + query;
        return $"https://{host}{EncodePath(path)}?{query}";
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    /// <summary>Keys lower-cased, keys and values percent-encoded, sorted by key: how v5 signs parameters and headers.</summary>
    private static (string Key, string Value)[] Canonical(IReadOnlyDictionary<string, string>? pairs) =>
        [.. (pairs ?? new Dictionary<string, string>())
            .Select(kv => (Key: Uri.EscapeDataString(kv.Key.ToLowerInvariant()), Value: Uri.EscapeDataString(kv.Value)))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)];

    /// <summary>Percent-encodes each segment of a key, keeping the slashes between them.</summary>
    public static string EncodePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
}

/// <summary>
/// The COS bucket that holds episode videos (Video:Provider = "Cos"). The bucket stays private: uploads, reads and
/// playback all use pre-signed links that expire. COS honours range requests, so a faststart MP4 starts playing
/// and seeks before the whole file has arrived.
///
/// The bucket is ReelGuo's own: when an episode or series is deleted, or a video replaced, the objects no episode uses
/// any more are deleted through <see cref="CosCleanup"/>. Existing objects are never overwritten.
/// </summary>
public sealed partial class CosVideoStore(VideoOptions options, TimeProvider clock, HttpClient http)
{
    private CosOptions Cos => options.Cos;
    private CosCredentials Credentials => new(Cos.SecretId, Cos.SecretKey);
    private string Host => $"{Cos.Bucket}.cos.{Cos.Region}.myqcloud.com";
    private string PlaybackHost => string.IsNullOrWhiteSpace(Cos.CdnHost) ? Host : Cos.CdnHost.Trim();
    private static string PathFor(string key) => "/" + key.TrimStart('/');

    public bool Enabled => options.UsesCos;
    public string Bucket => Cos.Bucket;
    public string Region => Cos.Region;
    public string DefaultFolder => CosKeys.FolderName(Cos.Folder) ?? "reelguo";

    private string Presign(string method, string host, string key, TimeSpan validFor, IReadOnlyDictionary<string, string>? query = null)
    {
        var now = clock.GetUtcNow();
        // Starts a few minutes back so a clock slightly behind COS's does not reject a fresh link.
        var url = CosSigner.Presign(method, host, PathFor(key), Credentials, now.AddMinutes(-5), now.Add(validFor), query);
        return string.IsNullOrWhiteSpace(Cos.Endpoint) ? url : Cos.Endpoint.TrimEnd('/') + url[("https://" + host).Length..];
    }

    /// <summary>One PUT of one object, for the admin's browser.</summary>
    public string UploadUrl(string key) => Presign("put", Host, key, TimeSpan.FromMinutes(Math.Clamp(Cos.UploadUrlMinutes, 5, 1440)));

    /// <summary>A viewer's playback link: GET only, and only until it expires.</summary>
    public (string Url, DateTimeOffset ExpiresAt) PlaybackUrl(string key)
    {
        var validFor = TimeSpan.FromMinutes(Math.Clamp(Cos.PlaybackUrlMinutes, 5, 720));
        return (Presign("get", PlaybackHost, key, validFor), clock.GetUtcNow().Add(validFor));
    }

    /// <summary>A read link for the worker's ffprobe and poster frame; always the bucket itself, never the CDN.</summary>
    public string ReadUrl(string key, TimeSpan validFor) => Presign("get", Host, key, validFor);

    /// <summary>
    /// Deletes one video. Only keys under videos/ are ever deleted; COS answers 204 whether or not the object existed.
    /// Callers go through <see cref="CosCleanup"/>, which first checks that no episode still uses the file.
    /// </summary>
    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        if (!CosKeys.IsCosKey(key)) throw new ArgumentException($"Not a COS video key: {key}", nameof(key));
        using var req = new HttpRequestMessage(HttpMethod.Delete, Presign("delete", Host, key, TimeSpan.FromMinutes(5)));
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return;
        res.EnsureSuccessStatusCode();
    }

    /// <summary>The stored object's size, or null when there is no such object.</summary>
    public async Task<long?> SizeAsync(string key, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Head, Presign("head", Host, key, TimeSpan.FromMinutes(5)));
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        return res.Content.Headers.ContentLength;
    }

    /// <summary>
    /// The storage folders under videos/ (what the COS console draws as folders), so the upload page can offer them.
    /// A thousand per page, followed to the end, with a ceiling against a runaway.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListFoldersAsync(CancellationToken ct)
    {
        var folders = new List<string>();
        string? marker = null;
        for (var page = 0; page < 20; page++)
        {
            var query = new Dictionary<string, string> { ["prefix"] = CosKeys.VideoRoot, ["delimiter"] = "/", ["max-keys"] = "1000" };
            if (marker is not null) query["marker"] = marker;
            using var res = await http.GetAsync(Presign("get", Host, "", TimeSpan.FromMinutes(5), query), ct);
            res.EnsureSuccessStatusCode();
            var xml = XDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            folders.AddRange(Named(xml, "CommonPrefixes").SelectMany(c => Named(c, "Prefix"))
                .Select(p => p.Value.Length > CosKeys.VideoRoot.Length ? p.Value[CosKeys.VideoRoot.Length..].TrimEnd('/') : "")
                .Where(n => n.Length > 0));
            if (Named(xml, "IsTruncated").FirstOrDefault()?.Value != "true") break;
            marker = Named(xml, "NextMarker").FirstOrDefault()?.Value;
            if (string.IsNullOrEmpty(marker)) break;
        }
        return [.. folders.Distinct().Order(StringComparer.OrdinalIgnoreCase)];

        static IEnumerable<XElement> Named(XContainer x, string name) => x.Descendants().Where(e => e.Name.LocalName == name);
    }

    /// <summary>
    /// Every object under <paramref name="prefix"/> (one title's folder) with its size: one listing request instead of
    /// asking after each episode. A thousand per page, with the same ceiling.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, long>> ListFilesAsync(string prefix, CancellationToken ct)
    {
        var files = new Dictionary<string, long>(StringComparer.Ordinal);
        string? marker = null;
        for (var page = 0; page < 20; page++)
        {
            var query = new Dictionary<string, string> { ["prefix"] = prefix, ["max-keys"] = "1000" };
            if (marker is not null) query["marker"] = marker;
            using var res = await http.GetAsync(Presign("get", Host, "", TimeSpan.FromMinutes(5), query), ct);
            res.EnsureSuccessStatusCode();
            var xml = XDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            string? last = null;
            foreach (var c in xml.Descendants().Where(e => e.Name.LocalName == "Contents"))
            {
                var key = c.Elements().FirstOrDefault(e => e.Name.LocalName == "Key")?.Value;
                var size = c.Elements().FirstOrDefault(e => e.Name.LocalName == "Size")?.Value;
                if (key is null) continue;
                last = key;
                files[key] = long.TryParse(size, CultureInfo.InvariantCulture, out var n) ? n : 0;
            }
            if (xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "IsTruncated")?.Value != "true") break;
            // Without a delimiter COS may leave NextMarker out; the last key listed is where the next page starts.
            marker = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "NextMarker")?.Value ?? last;
            if (string.IsNullOrEmpty(marker)) break;
        }
        return files;
    }

    /// <summary>What is missing or wrong in the settings, or null when COS can be used.</summary>
    public static string? Problem(VideoOptions o)
    {
        if (!o.UsesCos) return null;
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(o.Cos.SecretId)) missing.Add("Video:Cos:SecretId");
        if (string.IsNullOrWhiteSpace(o.Cos.SecretKey)) missing.Add("Video:Cos:SecretKey");
        if (string.IsNullOrWhiteSpace(o.Cos.Bucket)) missing.Add("Video:Cos:Bucket");
        if (string.IsNullOrWhiteSpace(o.Cos.Region)) missing.Add("Video:Cos:Region");
        if (missing.Count > 0) return $"Video:Provider is Cos but {string.Join(", ", missing)} not set.";
        if (!BucketPattern().IsMatch(o.Cos.Bucket)) return "Video:Cos:Bucket must be the full bucket name including the APPID, e.g. reelguo-1300000000.";
        if (!RegionPattern().IsMatch(o.Cos.Region)) return "Video:Cos:Region must look like ap-singapore.";
        if (CosKeys.FolderName(o.Cos.Folder) is null) return "Video:Cos:Folder must be a folder name (no slashes, at most 64 characters).";
        return null;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*-[0-9]{5,}$")]
    private static partial Regex BucketPattern();

    [GeneratedRegex("^[a-z]+(-[a-z]+)+(-[0-9]+)?$")]
    private static partial Regex RegionPattern();
}

public static class VideoSetup
{
    /// <summary>
    /// Video settings (bound from the "Video" section when first used, so every configuration source counts) and the
    /// COS store. With Provider = Cos and settings missing, the app refuses to start and says which.
    /// </summary>
    public static IServiceCollection AddReelGuoVideo(this IServiceCollection services)
    {
        services.AddOptions<VideoOptions>()
            .Configure<IConfiguration>((o, config) => config.GetSection(VideoOptions.Section).Bind(o))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<VideoOptions>, VideoOptionsValidator>();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<VideoOptions>>().Value);
        services.AddSingleton(sp => new CosVideoStore(sp.GetRequiredService<VideoOptions>(), sp.GetRequiredService<TimeProvider>(),
            new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(30) }));
        return services;
    }

    private sealed class VideoOptionsValidator : IValidateOptions<VideoOptions>
    {
        public ValidateOptionsResult Validate(string? name, VideoOptions options) =>
            CosVideoStore.Problem(options) is { } problem ? ValidateOptionsResult.Fail(problem) : ValidateOptionsResult.Success;
    }
}
