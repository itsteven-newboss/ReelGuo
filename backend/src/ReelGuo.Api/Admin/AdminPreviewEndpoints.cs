using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Data;
using ReelGuo.Data.Entities;
using ReelGuo.Data.Media;
using ReelGuo.Data.Storage;

namespace ReelGuo.Api.Admin;

/// <summary>
/// What the admin preview player loads. <c>Kind</c> is "hls" (a playlist) or "file" (one progressive file, usually MP4).
/// <c>Source</c> is "output" (what viewers get) or "original" (the uploaded file, before processing finished).
/// </summary>
public sealed record EpisodePreviewDto(
    long EpisodeId, string Kind, string Url, string Source, string? Note, string? PosterUrl, int Width, int Height, DateTimeOffset? ExpiresAt);

/// <summary>
/// Lets an admin watch an episode inside the series editor. Local media is streamed through these admin-only routes,
/// so the admin cookie is the only credential: nothing here is reachable by viewers, and neither country blocking nor
/// a takedown stops an admin from checking the video. COS videos play from short-lived signed COS links, as for viewers.
/// </summary>
public static class AdminPreviewEndpoints
{
    public enum PreviewKind { None, CosOutput, HlsOutput, CosOriginal, LocalOriginal }

    public static RouteGroupBuilder MapAdminPreview(this RouteGroupBuilder episodes)
    {
        string[] methods = [HttpMethods.Get, HttpMethods.Head];
        episodes.MapGet("/{id:long}/preview", GetPreview).WithSummary("Where the admin preview player loads this episode's video from.");
        // The version is part of the path so a re-encode never mixes old and new segments in the browser cache.
        episodes.MapMethods("/{id:long}/preview/hls/{version:int}/{**file}", methods, ServeHlsAsync).ExcludeFromDescription();
        episodes.MapMethods("/{id:long}/preview/source", methods, ServeSourceAsync).ExcludeFromDescription();
        return episodes;
    }

    /// <summary>
    /// What to play: the processed video when there is one (it is what viewers see), otherwise the uploaded original once
    /// the upload has finished. An episode still waiting for its first upload has nothing to show.
    /// </summary>
    public static PreviewKind Pick(Episode e, bool cosEnabled)
    {
        if (e.VideoKey is not null && cosEnabled) return PreviewKind.CosOutput;
        if (e.HlsPrefix is not null) return PreviewKind.HlsOutput;
        if (e.SourceKey is null || e.Status == EpisodeStatus.Uploading) return PreviewKind.None;
        if (CosKeys.IsCosKey(e.SourceKey)) return cosEnabled ? PreviewKind.CosOriginal : PreviewKind.None;
        return PreviewKind.LocalOriginal;
    }

    private static readonly HashSet<string> BrowserFriendly = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".webm", ".mov" };

    private static async Task<Results<Ok<EpisodePreviewDto>, NotFound<ApiErrorResponse>, Conflict<ApiErrorResponse>>> GetPreview(
        long id, HttpContext http, ReelGuoDbContext db, CosVideoStore cos, MediaUrlSigner signer, CancellationToken ct)
    {
        var e = await db.Episodes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "剧集不存在"));
        http.Response.Headers.CacheControl = "no-store";

        var kind = Pick(e, cos.Enabled);
        string? note = kind switch
        {
            PreviewKind.CosOutput or PreviewKind.HlsOutput when e.Status == EpisodeStatus.Processing => "新上传的视频还在处理，这里播放的是之前的版本",
            PreviewKind.CosOutput or PreviewKind.HlsOutput when e.Status == EpisodeStatus.Failed => "新上传的视频处理失败，这里播放的是之前的版本",
            PreviewKind.CosOriginal or PreviewKind.LocalOriginal when !BrowserFriendly.Contains(Path.GetExtension(e.SourceKey!)) =>
                "这是上传的原始文件，浏览器可能无法播放这种格式",
            PreviewKind.CosOriginal or PreviewKind.LocalOriginal => e.Status == EpisodeStatus.Failed
                ? "处理失败，这里播放的是上传的原始文件"
                : "还没处理完，这里播放的是上传的原始文件",
            _ => null,
        };
        var poster = signer.PublicUrlOrNull(e.PosterKey);
        var basePath = $"/api/admin/episodes/{e.Id}/preview";

        EpisodePreviewDto Dto(string type, string url, string source, DateTimeOffset? expiresAt = null) =>
            new(e.Id, type, url, source, note, poster, e.Width, e.Height, expiresAt);

        switch (kind)
        {
            case PreviewKind.CosOutput:
            {
                var (url, expiresAt) = cos.PlaybackUrl(e.VideoKey!);
                return TypedResults.Ok(Dto("file", url, "output", expiresAt));
            }
            case PreviewKind.HlsOutput:
                return TypedResults.Ok(Dto("hls", $"{basePath}/hls/{e.MediaVersion}/master.m3u8", "output"));
            case PreviewKind.CosOriginal:
            {
                var (url, expiresAt) = cos.PlaybackUrl(e.SourceKey!);
                return TypedResults.Ok(Dto("file", url, "original", expiresAt));
            }
            case PreviewKind.LocalOriginal:
                return TypedResults.Ok(Dto("file", $"{basePath}/source", "original"));
            default:
                return TypedResults.Conflict(ApiErrorResponse.Of("no_preview",
                    e.SourceKey is null ? "这一集还没有上传视频" : "视频还在上传，传完后才能预览"));
        }
    }

    private static async Task<IResult> ServeHlsAsync(long id, int version, string file, HttpContext http, ReelGuoDbContext db,
        StorageBuckets storage, CancellationToken ct)
    {
        var e = await db.Episodes.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.HlsPrefix, x.MediaVersion }).FirstOrDefaultAsync(ct);
        // A stale version (re-encoded since the dialog opened) is gone; the player asks for a new preview link.
        if (e?.HlsPrefix is null || e.MediaVersion != version) return Results.NotFound();
        var key = e.HlsPrefix + file;
        if (!StorageKeys.IsValid(key) || MediaPaths.SignedPrefixOf(key) != e.HlsPrefix) return Results.NotFound();
        return await StreamAsync(storage.Media, key, "private, max-age=3600", http, ct);
    }

    private static async Task<IResult> ServeSourceAsync(long id, HttpContext http, ReelGuoDbContext db, StorageBuckets storage, CancellationToken ct)
    {
        var key = await db.Episodes.AsNoTracking().Where(x => x.Id == id).Select(x => x.SourceKey).FirstOrDefaultAsync(ct);
        if (key is null || CosKeys.IsCosKey(key) || !StorageKeys.IsValid(key)) return Results.NotFound();
        return await StreamAsync(storage.Raw, key, "no-store", http, ct);
    }

    private static async Task<IResult> StreamAsync(IObjectStorage bucket, string key, string cacheControl, HttpContext http, CancellationToken ct)
    {
        var stored = await bucket.OpenReadAsync(key, ct);
        if (stored is null) return Results.NotFound();
        http.Response.Headers.CacheControl = cacheControl;
        return Results.Stream(stored.Content, StorageKeys.ContentTypeFor(key), enableRangeProcessing: stored.Content.CanSeek);
    }
}
