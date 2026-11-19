using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Api.Public;
using ReelGuo.Data;
using ReelGuo.Data.Entities;
using ReelGuo.Data.Media;
using ReelGuo.Data.Storage;

namespace ReelGuo.Api.Admin;

public sealed record AdminAppReleaseDto(
    int Id, string Platform, string Version, string Url, string? Signature, string? Sha256, string Notes, bool Mandatory, bool Published,
    DateTimeOffset? PublishedAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string UpdatedBy);

public sealed record AppReleaseInput(string Platform, string Version, string Url, string? Signature, string? Sha256, string? Notes, bool Mandatory, bool Published);

/// <summary>An uploaded installer: the address to use as the release's download URL, and its SHA-256.</summary>
public sealed record AppFileDto(string Url, string Name, long Size, string Sha256);

/// <summary>App 版本: native app builds offered to the desktop updater, the Android update prompt and the download page.</summary>
public static class AdminAppReleasesEndpoints
{
    /// <summary>Where uploaded installers are kept: p/app/{platform}/{yyyyMMddHHmmss}{random}/{file name}.</summary>
    private const string UploadPrefix = "p/app/";
    private const long MaxFileBytes = 500L * 1024 * 1024;

    /// <summary>
    /// The files each platform takes, by name ending, with the bytes such a file starts with. The desktop updater installs
    /// the same file, so macOS takes the .app.tar.gz (the updater can't use a .dmg).
    /// </summary>
    private static readonly Dictionary<string, (string Ending, byte[] Magic)[]> FileTypes = new()
    {
        ["android"] = [(".apk", [0x50, 0x4B, 0x03, 0x04])],
        ["windows-x86_64"] = [(".exe", [0x4D, 0x5A]), (".msi", [0xD0, 0xCF, 0x11, 0xE0])],
        ["darwin-aarch64"] = [(".tar.gz", [0x1F, 0x8B])],
        ["darwin-x86_64"] = [(".tar.gz", [0x1F, 0x8B])],
        ["linux-x86_64"] = [(".AppImage", [0x7F, 0x45, 0x4C, 0x46])],
    };

    public static RouteGroupBuilder MapAdminAppReleases(this RouteGroupBuilder admin)
    {
        var releases = admin.MapGroup("/app-releases").WithTags("Admin: app releases");
        releases.MapGet("/", List).WithSummary("Every release, newest first.");
        releases.MapPost("/", Create).WithSummary("Adds a release (unpublished releases are not offered to anyone).");
        releases.MapPut("/{id:int}", Update).WithSummary("Edits, publishes or withdraws a release.");
        releases.MapDelete("/{id:int}", Delete).WithSummary("Deletes a release.");
        releases.MapPost("/files", Upload)
            .WithSummary("Uploads an installer as the raw request body (?platform=…&name=<file name>); returns its address and SHA-256 for the release.");
        return admin;
    }

    private static AdminAppReleaseDto ToDto(AppRelease r) => new(r.Id, r.Platform, r.Version, r.Url, r.Signature, r.Sha256, r.Notes, r.Mandatory,
        r.Published, r.PublishedAt, r.CreatedAt, r.UpdatedAt, r.UpdatedBy);

    private static async Task<Ok<List<AdminAppReleaseDto>>> List(ReelGuoDbContext db, CancellationToken ct) =>
        TypedResults.Ok((await db.AppReleases.AsNoTracking().ToListAsync(ct))
            .OrderBy(r => Array.IndexOf(AppVersions.Platforms, r.Platform))
            .ThenByDescending(r => AppVersions.Parse(r.Version))
            .Select(ToDto).ToList());

    private static string? Validate(AppReleaseInput input, MediaUrlSigner signer)
    {
        var platform = (input.Platform ?? "").Trim().ToLowerInvariant();
        if (!AppVersions.Platforms.Contains(platform)) return $"平台需为 {string.Join(" / ", AppVersions.Platforms)} 之一";
        if (AppVersions.Parse(input.Version) is null) return "版本号需为 主.次.修订 格式，如 0.6.0";
        // An installer uploaded here has a site-relative address (/media/p/app/…), so it downloads from whichever of our
        // domains the visitor is on.
        var address = (input.Url ?? "").Trim();
        if (UploadedKey(address, signer) is null
            && (!Uri.TryCreate(address, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps
                && !(url.Scheme == Uri.UriSchemeHttp && url.IsLoopback) && !(platform == "ios" && url.Scheme == "itms-services")))
            return platform == "ios" ? "安装链接需为 https:// 或 itms-services:// 开头的完整地址" : "下载地址需为 https:// 开头的完整地址，或直接上传安装包";
        if (AppVersions.IsDesktop(platform) && input.Published && string.IsNullOrWhiteSpace(input.Signature))
            return "桌面版发布前需填写更新签名（.sig 文件内容），否则自动更新会拒绝安装";
        if ((input.Signature ?? "").Length > 2000) return "签名过长";
        if (input.Sha256 is { Length: > 0 } sha && (sha.Trim().Length != 64 || !sha.Trim().All(Uri.IsHexDigit))) return "SHA-256 需为 64 位十六进制";
        if ((input.Notes ?? "").Length > 2000) return "更新说明不超过 2000 字";
        return null;
    }

    private static void Apply(AppRelease r, AppReleaseInput input, DateTimeOffset now, string actor)
    {
        r.Platform = input.Platform.Trim().ToLowerInvariant();
        r.Version = AppVersions.Parse(input.Version)!.ToString(3);
        r.Url = input.Url.Trim();
        r.Signature = string.IsNullOrWhiteSpace(input.Signature) ? null : input.Signature.Trim();
        r.Sha256 = string.IsNullOrWhiteSpace(input.Sha256) ? null : input.Sha256.Trim().ToLowerInvariant();
        r.Notes = (input.Notes ?? "").Trim();
        r.Mandatory = input.Mandatory;
        if (input.Published && !r.Published) r.PublishedAt = now;
        r.Published = input.Published;
        r.UpdatedAt = now;
        r.UpdatedBy = actor;
    }

    private static async Task<Results<Ok<AdminAppReleaseDto>, BadRequest<ApiErrorResponse>, Conflict<ApiErrorResponse>>> Create(
        AppReleaseInput input, HttpContext http, ReelGuoDbContext db, MediaUrlSigner signer, StorageBuckets storage, TimeProvider time,
        ILoggerFactory logs, CancellationToken ct)
    {
        if (Validate(input, signer) is { } error) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_release", error));
        var now = time.GetUtcNow();
        var actor = AdminActor.From(http);
        var release = new AppRelease { Platform = "", Version = "", Url = "", UpdatedBy = actor, CreatedAt = now };
        Apply(release, input, now, actor);
        if (await db.AppReleases.AnyAsync(r => r.Platform == release.Platform && r.Version == release.Version, ct))
            return TypedResults.Conflict(ApiErrorResponse.Of("release_exists", $"{release.Platform} {release.Version} 已存在"));
        db.AppReleases.Add(release);
        await db.SaveChangesAsync(ct);
        db.AddAudit(now, actor, "app_release.create", "app_release", release.Id.ToString(), new { release.Platform, release.Version, release.Published, release.Mandatory });
        await db.SaveChangesAsync(ct);
        await RemoveUnusedFilesAsync(db, storage, signer, now, logs);
        return TypedResults.Ok(ToDto(release));
    }

    private static async Task<Results<Ok<AdminAppReleaseDto>, NotFound<ApiErrorResponse>, BadRequest<ApiErrorResponse>, Conflict<ApiErrorResponse>>> Update(
        int id, AppReleaseInput input, HttpContext http, ReelGuoDbContext db, MediaUrlSigner signer, StorageBuckets storage, TimeProvider time,
        ILoggerFactory logs, CancellationToken ct)
    {
        var release = await db.AppReleases.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (release is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "版本不存在"));
        if (Validate(input, signer) is { } error) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_release", error));
        var now = time.GetUtcNow();
        var actor = AdminActor.From(http);
        var wasPublished = release.Published;
        Apply(release, input, now, actor);
        if (await db.AppReleases.AnyAsync(r => r.Id != id && r.Platform == release.Platform && r.Version == release.Version, ct))
            return TypedResults.Conflict(ApiErrorResponse.Of("release_exists", $"{release.Platform} {release.Version} 已存在"));
        var action = !wasPublished && release.Published ? "app_release.publish" : wasPublished && !release.Published ? "app_release.withdraw" : "app_release.update";
        db.AddAudit(now, actor, action, "app_release", id.ToString(), new { release.Platform, release.Version, release.Published, release.Mandatory });
        await db.SaveChangesAsync(ct);
        await RemoveUnusedFilesAsync(db, storage, signer, now, logs);
        return TypedResults.Ok(ToDto(release));
    }

    private static async Task<Results<NoContent, NotFound<ApiErrorResponse>>> Delete(
        int id, HttpContext http, ReelGuoDbContext db, MediaUrlSigner signer, StorageBuckets storage, TimeProvider time, ILoggerFactory logs, CancellationToken ct)
    {
        var release = await db.AppReleases.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (release is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "版本不存在"));
        db.AppReleases.Remove(release);
        db.AddAudit(time, AdminActor.From(http), "app_release.delete", "app_release", id.ToString(), new { release.Platform, release.Version });
        await db.SaveChangesAsync(ct);
        await RemoveUnusedFilesAsync(db, storage, signer, time.GetUtcNow(), logs, also: UploadedKey(release.Url, signer));
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<AppFileDto>, BadRequest<ApiErrorResponse>>> Upload(
        HttpContext http, StorageBuckets storage, MediaUrlSigner signer, TimeProvider time, ILoggerFactory logs, CancellationToken ct)
    {
        var platform = ((string?)http.Request.Query["platform"] ?? "").Trim().ToLowerInvariant();
        var name = StorageKeys.SanitizeFileName((string?)http.Request.Query["name"] ?? "");
        if (platform == "ios") return Bad("苹果版没有安装包可上传：留空即可（苹果用户会下载主屏幕描述文件），或填写签名平台提供的安装链接");
        if (!FileTypes.TryGetValue(platform, out var types)) return Bad("未知的平台");
        var type = types.FirstOrDefault(t => name.EndsWith(t.Ending, StringComparison.OrdinalIgnoreCase));
        if (type.Ending is null) return Bad($"该平台的安装包需为 {string.Join(" 或 ", types.Select(t => t.Ending))} 文件");
        if (http.Request.ContentLength is > MaxFileBytes) return Bad("安装包不能超过 500 MB");
        var sizeLimit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeLimit is { IsReadOnly: false }) sizeLimit.MaxRequestBodySize = MaxFileBytes;

        // A folder per upload: a new version never replaces a file someone may be downloading.
        var key = $"{UploadPrefix}{platform}/{time.GetUtcNow():yyyyMMddHHmmss}{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2))}/{name}";
        await using var body = new HashingStream(http.Request.Body);
        await storage.Media.PutAsync(key, body, StorageKeys.ContentTypeFor(key), ct);
        if (body.BytesRead == 0 || !body.Head.StartsWith(type.Magic))
        {
            await DeleteQuietly(storage, key, logs);
            return Bad(body.BytesRead == 0 ? "文件是空的" : $"文件内容不是有效的 {type.Ending} 安装包");
        }
        return TypedResults.Ok(new AppFileDto(signer.PublicUrl(key), name, body.BytesRead, body.Sha256()));

        static BadRequest<ApiErrorResponse> Bad(string message) => TypedResults.BadRequest(ApiErrorResponse.Of("invalid_file", message));
    }

    /// <summary>The storage key of an installer uploaded here, from its address; null for an address elsewhere.</summary>
    private static string? UploadedKey(string url, MediaUrlSigner signer)
    {
        var prefix = signer.PublicUrl(UploadPrefix);
        var key = url.StartsWith(prefix, StringComparison.Ordinal) ? UploadPrefix + url[prefix.Length..] : null;
        return key is not null && StorageKeys.IsValid(key) ? key : null;
    }

    /// <summary>
    /// Deletes uploaded installers no release uses: <paramref name="also"/> (a deleted release's file) at once, and uploads
    /// whose form was never saved after a day (so a form still open keeps its file). Failures only leave a file behind.
    /// </summary>
    private static async Task RemoveUnusedFilesAsync(
        ReelGuoDbContext db, StorageBuckets storage, MediaUrlSigner signer, DateTimeOffset now, ILoggerFactory logs, string? also = null)
    {
        try
        {
            var used = (await db.AppReleases.Select(r => r.Url).ToListAsync()).Select(u => UploadedKey(u, signer)).OfType<string>().ToHashSet();
            foreach (var key in await storage.Media.ListAsync(UploadPrefix, CancellationToken.None))
            {
                if (used.Contains(key)) continue;
                var parts = key.Split('/');
                var stale = parts.Length == 5 && parts[3].Length >= 14
                    && DateTimeOffset.TryParseExact(parts[3][..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var uploaded)
                    && now - uploaded > TimeSpan.FromDays(1);
                if (stale || key == also) await DeleteQuietly(storage, key, logs);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logs.CreateLogger("ReelGuo.Api.Admin.AppReleases").LogWarning(ex, "Could not clean up unused app installers");
        }
    }

    private static async Task DeleteQuietly(StorageBuckets storage, string key, ILoggerFactory logs)
    {
        try { await storage.Media.DeleteAsync(key, CancellationToken.None); }
        catch (Exception ex) { logs.CreateLogger("ReelGuo.Api.Admin.AppReleases").LogWarning(ex, "Could not delete the unused app installer {Key}", key); }
    }

    /// <summary>Passes an upload through, counting and hashing (SHA-256) it, and keeping its first bytes for the type check.</summary>
    private sealed class HashingStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly byte[] _head = new byte[8];
        private int _headLength;

        public long BytesRead { get; private set; }
        public ReadOnlySpan<byte> Head => _head.AsSpan(0, _headLength);
        public string Sha256() => Convert.ToHexStringLower(_hash.GetCurrentHash());

        public override int Read(byte[] buffer, int offset, int count) => Took(buffer.AsSpan(offset, inner.Read(buffer, offset, count)));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            return Took(buffer.Span[..read]);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Took(ReadOnlySpan<byte> read)
        {
            if (_headLength < _head.Length)
            {
                var take = Math.Min(_head.Length - _headLength, read.Length);
                read[..take].CopyTo(_head.AsSpan(_headLength));
                _headLength += take;
            }
            _hash.AppendData(read);
            BytesRead += read.Length;
            return read.Length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }
    }
}
