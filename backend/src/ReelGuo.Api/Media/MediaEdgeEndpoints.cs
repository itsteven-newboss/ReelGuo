using Microsoft.Net.Http.Headers;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Data.Media;
using ReelGuo.Data.Storage;

namespace ReelGuo.Api.Media;

/// <summary>
/// Serves media the same way the CDN edge does in production (see edge/): public images under /media/p/,
/// signed episode files under /media/m/{exp}/{sig}/. Country blocking applies to both through
/// GeoBlockingMiddleware. Useful on its own for development and small deployments without a CDN.
/// </summary>
public static class MediaEdgeEndpoints
{
    public static IEndpointRouteBuilder MapMediaEdge(this IEndpointRouteBuilder app)
    {
        string[] methods = [HttpMethods.Get, HttpMethods.Head];

        app.MapMethods("/media/p/{**rest}", methods, (string rest, HttpContext http, StorageBuckets storage, CancellationToken ct) =>
                ServeAsync(MediaPaths.PublicPrefix + rest, http, storage, ct))
            .ExcludeFromDescription();

        app.MapMethods("/media/m/{exp:long}/{sig}/{**rest}", methods, ServeSignedAsync).ExcludeFromDescription();

        return app;
    }

    private static async Task<IResult> ServeSignedAsync(long exp, string sig, string rest, HttpContext http, StorageBuckets storage,
        MediaUrlSigner signer, IRevokedSeriesStore revoked, CancellationToken ct)
    {
        if (!signer.IsValid(exp, sig, rest))
            return ApiResults.Error(StatusCodes.Status403Forbidden, "media_token_invalid", "播放链接已失效，请刷新");
        // A taken-down series stops playing at once, even through links issued before the takedown.
        if (MediaPaths.SeriesIdOf(rest) is { } seriesId && (await revoked.GetAsync(ct)).Contains(seriesId))
        {
            http.Response.Headers.CacheControl = "no-store";
            return ApiResults.Error(StatusCodes.Status410Gone, "media_revoked", "该内容已下架");
        }
        return await ServeAsync(rest, http, storage, ct);
    }

    private static async Task<IResult> ServeAsync(string key, HttpContext http, StorageBuckets storage, CancellationToken ct)
    {
        if (!StorageKeys.IsValid(key)) return Results.NotFound();
        var stored = await storage.Media.OpenReadAsync(key, ct);
        if (stored is null) return Results.NotFound();

        var headers = http.Response.Headers;
        headers.AccessControlAllowOrigin = "*";
        headers.CacheControl = CacheControlFor(key);
        var contentType = StorageKeys.ContentTypeFor(key);
        return Results.Stream(stored.Content, contentType, enableRangeProcessing: stored.Content.CanSeek);
    }

    /// <summary>Segments and images never change (new encodes get a new version folder); playlists are short-lived.</summary>
    private static string CacheControlFor(string key) => Path.GetExtension(key) switch
    {
        ".m3u8" => "public, max-age=60",
        _ => "public, max-age=31536000, immutable",
    };
}
