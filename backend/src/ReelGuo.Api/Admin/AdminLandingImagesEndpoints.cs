using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Data;
using ReelGuo.Data.Entities;
using ReelGuo.Data.Media;
using ReelGuo.Data.Storage;

namespace ReelGuo.Api.Admin;

public sealed record AdminLandingImageDto(string Slot, string Url, DateTimeOffset UpdatedAt, string UpdatedBy);

/// <param name="Images">Slot id → image URL, for the slots that have an uploaded image.</param>
public sealed record LandingImagesDto(Dictionary<string, string> Images);

/// <summary>
/// 下载页图片: images for the app download landing page (/app), one per slot. A slot without an upload keeps the page's
/// default, built from series covers; uploading replaces it, 恢复默认 removes the upload again.
/// </summary>
public static class AdminLandingImagesEndpoints
{
    /// <summary>
    /// The slots. Backgrounds come in a desktop (<c>_d</c>) and a phone (<c>_p</c>) version; either serves the other screen
    /// when only one is uploaded. The rest are pictures inside a section.
    /// </summary>
    public static readonly IReadOnlyList<string> Slots =
    [
        "home_d", "home_p",
        "genres_bg_d", "genres_bg_p", "genre_1", "genre_2", "genre_3", "genre_4",
        "experience_bg_d", "experience_bg_p", "experience_screen", "experience_poster_1", "experience_poster_2", "experience_poster_3", "experience_poster_4",
        "wishlist_bg_d", "wishlist_bg_p",
        "creator_d", "creator_p",
        "about_d", "about_p",
    ];

    private const long MaxBytes = 10 * 1024 * 1024;

    public static RouteGroupBuilder MapAdminLandingImages(this RouteGroupBuilder admin)
    {
        var images = admin.MapGroup("/landing-images").WithTags("Admin: landing images");
        images.MapGet("/", List).WithSummary("The uploaded download-page images (slots without one use series covers).");
        images.MapPost("/{slot}", Upload).WithSummary("Uploads a slot's image (JPEG, PNG or WebP, up to 10 MB) as the raw request body.");
        images.MapDelete("/{slot}", Reset).WithSummary("Removes a slot's image, so the page goes back to its default.");
        return admin;
    }

    /// <summary>Public: the download page reads its images here.</summary>
    public static RouteGroupBuilder MapLandingImages(this RouteGroupBuilder app)
    {
        app.MapGet("/landing-images", Public).WithSummary("Images uploaded for the download page, by slot.");
        return app;
    }

    private static AdminLandingImageDto ToDto(LandingImage i, MediaUrlSigner signer) => new(i.Slot, signer.PublicUrl(i.Key), i.UpdatedAt, i.UpdatedBy);

    private static async Task<Ok<List<AdminLandingImageDto>>> List(ReelGuoDbContext db, MediaUrlSigner signer, CancellationToken ct) =>
        TypedResults.Ok((await db.LandingImages.AsNoTracking().ToListAsync(ct))
            .OrderBy(i => Slots.ToList().IndexOf(i.Slot)).Select(i => ToDto(i, signer)).ToList());

    private static async Task<Ok<LandingImagesDto>> Public(ReelGuoDbContext db, MediaUrlSigner signer, CancellationToken ct) =>
        TypedResults.Ok(new LandingImagesDto((await db.LandingImages.AsNoTracking().ToListAsync(ct))
            .Where(i => Slots.Contains(i.Slot)).ToDictionary(i => i.Slot, i => signer.PublicUrl(i.Key))));

    private static async Task<Results<Ok<AdminLandingImageDto>, NotFound<ApiErrorResponse>, BadRequest<ApiErrorResponse>>> Upload(
        string slot, HttpContext http, ReelGuoDbContext db, StorageBuckets storage, MediaUrlSigner signer, TimeProvider time,
        ILoggerFactory logs, CancellationToken ct)
    {
        if (!Slots.Contains(slot)) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "没有这个图片位置"));
        if (http.Request.ContentLength is > MaxBytes) return TypedResults.BadRequest(ApiErrorResponse.Of("image_too_large", "图片不能超过 10 MB"));

        using var buffer = new MemoryStream();
        await http.Request.Body.CopyToAsync(buffer, ct);
        if (buffer.Length is 0 or > MaxBytes) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_image", "图片为空或超过 10 MB"));
        var ext = ImageExtension(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        if (ext is null) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_image", "图片需为 JPEG、PNG 或 WebP 格式"));

        // A new key per upload, so browsers and CDNs never show the old picture from cache.
        var key = $"p/landing/{slot}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}{ext}";
        buffer.Position = 0;
        await storage.Media.PutAsync(key, buffer, StorageKeys.ContentTypeFor(key), ct);

        var actor = AdminActor.From(http);
        var now = time.GetUtcNow();
        var row = await db.LandingImages.FirstOrDefaultAsync(i => i.Slot == slot, ct);
        var previous = row?.Key;
        if (row is null) db.LandingImages.Add(row = new LandingImage { Slot = slot, Key = key, UpdatedAt = now, UpdatedBy = actor });
        else { row.Key = key; row.UpdatedAt = now; row.UpdatedBy = actor; }
        db.AddAudit(now, actor, "landing_image.set", "landing_image", slot);
        await db.SaveChangesAsync(ct);

        if (previous is not null) await DeleteQuietly(storage, previous, logs);
        return TypedResults.Ok(ToDto(row, signer));
    }

    private static async Task<Results<NoContent, NotFound<ApiErrorResponse>>> Reset(
        string slot, HttpContext http, ReelGuoDbContext db, StorageBuckets storage, TimeProvider time, ILoggerFactory logs, CancellationToken ct)
    {
        if (!Slots.Contains(slot)) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "没有这个图片位置"));
        var row = await db.LandingImages.FirstOrDefaultAsync(i => i.Slot == slot, ct);
        if (row is null) return TypedResults.NoContent();
        db.LandingImages.Remove(row);
        db.AddAudit(time, AdminActor.From(http), "landing_image.reset", "landing_image", slot);
        await db.SaveChangesAsync(ct);
        await DeleteQuietly(storage, row.Key, logs);
        return TypedResults.NoContent();
    }

    /// <summary>Removes a replaced image file; a failure only leaves an unused file behind.</summary>
    private static async Task DeleteQuietly(StorageBuckets storage, string key, ILoggerFactory logs)
    {
        try { await storage.Media.DeleteAsync(key, CancellationToken.None); }
        catch (Exception ex) { logs.CreateLogger("ReelGuo.Api.Admin.LandingImages").LogWarning(ex, "Could not delete the replaced download-page image {Key}", key); }
    }

    /// <summary>Identifies the image type from its first bytes rather than trusting the file name.</summary>
    private static string? ImageExtension(ReadOnlySpan<byte> b) =>
        b.Length > 12 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? ".jpg"
        : b.Length > 12 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 ? ".png"
        : b.Length > 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8) ? ".webp"
        : null;
}
