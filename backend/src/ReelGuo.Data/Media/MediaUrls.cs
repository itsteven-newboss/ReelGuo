using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ReelGuo.Data.Media;

public sealed class MediaOptions
{
    public const string Section = "Media";

    /// <summary>
    /// Base URL for media. In development the API's own edge (<c>/media</c>); in production the CDN host,
    /// e.g. <c>https://media.example.com</c>, where the edge worker in <c>edge/</c> applies the same rules.
    /// </summary>
    public string PublicBaseUrl { get; set; } = "/media";

    /// <summary>Base64 HMAC key (32+ bytes) shared with the CDN edge. Required.</summary>
    public string SigningKey { get; set; } = "";

    /// <summary>How long a playback URL stays valid.</summary>
    public int PlaybackUrlMinutes { get; set; } = 240;
}

/// <summary>
/// Media URL layout (shared with the edge worker; keep both in sync):
///   public  : {base}/p/{rest}                         images, no token
///   signed  : {base}/m/{exp}/{sig}/s/{series}/e/{ep}/v{version}/{file}
/// The signature covers "{exp}:{prefix}" where prefix = "s/{series}/e/{ep}/v{version}/". Playlists use
/// relative URIs, which keep the signed path prefix, so one token covers every file of an episode.
/// </summary>
public static partial class MediaPaths
{
    public static string EpisodePrefix(long seriesId, int episode, int version) => $"s/{seriesId}/e/{episode}/v{version}/";

    public const string PublicPrefix = "p/";

    public static string PosterKey(long seriesId, int episode, int version) => $"p/posters/{seriesId}-{episode}-v{version}.jpg";

    public static string CoverKey(long seriesId, string extension) =>
        $"p/covers/{seriesId}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}{extension}";

    /// <summary>Returns the signed prefix of a media path, or null if the path is not inside an episode folder.</summary>
    public static string? SignedPrefixOf(string path)
    {
        var m = EpisodePrefixPattern().Match(path);
        return m.Success ? m.Value : null;
    }

    /// <summary>The series id of an episode media path ("s/{id}/e/..."), or null for anything else.</summary>
    public static long? SeriesIdOf(string path)
    {
        var m = EpisodePrefixPattern().Match(path);
        return m.Success ? long.Parse(m.Groups[1].ValueSpan) : null;
    }

    [GeneratedRegex(@"^s/(\d{1,18})/e/\d{1,6}/v\d{1,6}/")]
    private static partial Regex EpisodePrefixPattern();
}

public sealed class MediaUrlSigner
{
    private readonly byte[] _key;
    private readonly string _base;
    private readonly TimeSpan _validFor;
    private readonly TimeProvider _time;

    public MediaUrlSigner(MediaOptions options, TimeProvider time)
    {
        if (string.IsNullOrWhiteSpace(options.SigningKey))
            throw new InvalidOperationException("Media:SigningKey is not set.");
        _key = Convert.FromBase64String(options.SigningKey);
        if (_key.Length < 32) throw new InvalidOperationException("Media:SigningKey must be at least 32 bytes (base64).");
        _base = options.PublicBaseUrl.TrimEnd('/');
        _validFor = TimeSpan.FromMinutes(Math.Max(5, options.PlaybackUrlMinutes));
        _time = time;
    }

    public string PublicUrl(string key) => $"{_base}/{key}";

    public string? PublicUrlOrNull(string? key) => key is null ? null : PublicUrl(key);

    public (string Url, DateTimeOffset ExpiresAt) PlaybackUrl(string episodePrefix, string file = "master.m3u8")
    {
        var expiresAt = _time.GetUtcNow().Add(_validFor);
        var exp = expiresAt.ToUnixTimeSeconds();
        return ($"{_base}/m/{exp}/{Sign(exp, episodePrefix)}/{episodePrefix}{file}", expiresAt);
    }

    /// <summary>base64url(HMAC-SHA256(key, "{exp}:{prefix}")) without padding.</summary>
    public string Sign(long exp, string prefix)
    {
        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{exp}:{prefix}"));
        return Convert.ToBase64String(mac).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Checks a signed request. <paramref name="path"/> is everything after the signature.</summary>
    public bool IsValid(long exp, string signature, string path)
    {
        if (exp < _time.GetUtcNow().ToUnixTimeSeconds()) return false;
        var prefix = MediaPaths.SignedPrefixOf(path);
        if (prefix is null) return false;
        var expected = Encoding.ASCII.GetBytes(Sign(exp, prefix));
        var actual = Encoding.ASCII.GetBytes(signature);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
