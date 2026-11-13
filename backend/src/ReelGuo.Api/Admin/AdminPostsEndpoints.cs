using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Data;
using ReelGuo.Data.Catalog;
using ReelGuo.Data.Content;
using ReelGuo.Data.Entities;
using ReelGuo.Data.Media;
using ReelGuo.Data.Storage;

namespace ReelGuo.Api.Admin;

public sealed record AdminPostInput(string Title, string? Summary, string? ContentMarkdown, List<string>? Tags, long? SeriesId);

public sealed record AdminPostDto(
    long Id, string Title, string Summary, string ContentMarkdown, string ContentHtml, string? CoverUrl, List<string> Tags,
    long? SeriesId, string? SeriesTitle, PostStatus Status, DateTimeOffset? PublishedAt, long ViewCount, DateTimeOffset UpdatedAt);

public sealed record AdminPostListItem(long Id, string Title, string? CoverUrl, PostStatus Status, DateTimeOffset? PublishedAt, long ViewCount, DateTimeOffset UpdatedAt);

public sealed record AdminPostPage(List<AdminPostListItem> Items, int Page, int Size, int Total);

public sealed record MarkdownPreviewRequest(string Markdown);

public sealed record HtmlDto(string Html);

public sealed record ImageUrlDto(string Url);

public sealed record AdminRankRow(int Position, long SeriesId, string Title, double MetricValue, DateTimeOffset ComputedAt);

public static class AdminPostsEndpoints
{
    private const long MaxImageBytes = 5 * 1024 * 1024;

    public static RouteGroupBuilder MapAdminPosts(this RouteGroupBuilder admin)
    {
        var posts = admin.MapGroup("/posts").WithTags("Admin: posts");
        posts.MapGet("/", List).WithSummary("吃瓜 posts, newest first.");
        posts.MapPost("/", Create).WithSummary("Creates a draft post.");
        posts.MapPost("/preview", (MarkdownPreviewRequest r, PostRenderer renderer) => TypedResults.Ok(new HtmlDto(renderer.Render(r.Markdown))))
            .WithSummary("Renders Markdown exactly as it will be published.");
        posts.MapGet("/{id:long}", Get);
        posts.MapPut("/{id:long}", Update).WithSummary("Updates title, summary, Markdown, tags and the linked series.");
        posts.MapPost("/{id:long}/cover", (long id, HttpContext http, ReelGuoDbContext db, StorageBuckets storage, MediaUrlSigner signer, TimeProvider time, CancellationToken ct) =>
            UploadImage(id, http, db, storage, signer, time, isCover: true, ct)).WithSummary("Uploads the cover (JPEG/PNG/WebP, 5 MB) as the raw body.");
        posts.MapPost("/{id:long}/images", (long id, HttpContext http, ReelGuoDbContext db, StorageBuckets storage, MediaUrlSigner signer, TimeProvider time, CancellationToken ct) =>
            UploadImage(id, http, db, storage, signer, time, isCover: false, ct)).WithSummary("Uploads an image to embed in the article; returns its URL.");
        posts.MapPost("/{id:long}/publish", Publish);
        posts.MapPost("/{id:long}/unpublish", Unpublish);
        posts.MapDelete("/{id:long}", Delete);

        var ranks = admin.MapGroup("/ranks").WithTags("Admin: rankings");
        ranks.MapGet("/{kind}", ListRanks).WithSummary("Current ranking rows (before per-country filtering).");
        ranks.MapPost("/recompute", async (RankingCalculator calculator, CancellationToken ct) =>
                await calculator.RecomputeAsync(ct) is { } run
                    ? Results.Ok(run)
                    : ApiResults.Error(StatusCodes.Status409Conflict, "busy", "榜单正在计算中，请稍后"))
            .WithSummary("Recomputes rankings now (the worker also does this every 10 minutes).");
        return admin;
    }

    private static AdminPostDto ToDto(Post p, MediaUrlSigner signer) => new(
        p.Id, p.Title, p.Summary, p.ContentMarkdown, p.ContentHtml, signer.PublicUrlOrNull(p.CoverKey), p.Tags,
        p.SeriesId, p.Series?.Title, p.Status, p.PublishedAt, p.ViewCount, p.UpdatedAt);

    private static async Task<Ok<AdminPostPage>> List(ReelGuoDbContext db, MediaUrlSigner signer, [FromQuery] int? page, CancellationToken ct)
    {
        var p = Math.Max(1, page ?? 1);
        const int size = 20;
        var total = await db.Posts.CountAsync(ct);
        var rows = await db.Posts.AsNoTracking().OrderByDescending(x => x.UpdatedAt).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return TypedResults.Ok(new AdminPostPage(
            rows.Select(x => new AdminPostListItem(x.Id, x.Title, signer.PublicUrlOrNull(x.CoverKey), x.Status, x.PublishedAt, x.ViewCount, x.UpdatedAt)).ToList(),
            p, size, total));
    }

    private static async Task<string?> ApplyAsync(Post post, AdminPostInput input, ReelGuoDbContext db, PostRenderer renderer, CancellationToken ct)
    {
        var title = (input.Title ?? "").Trim();
        if (title.Length is 0 or > 200) return "标题需为 1-200 个字符";
        var summary = (input.Summary ?? "").Trim();
        if (summary.Length > 500) return "摘要不能超过 500 个字符";
        var tags = (input.Tags ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList();
        if (tags.Count > 10 || tags.Any(t => t.Length > 20)) return "最多 10 个标签，每个不超过 20 个字符";
        if (input.SeriesId is { } sid && !await db.Series.AnyAsync(s => s.Id == sid, ct)) return "关联的剧集不存在";

        post.Title = title;
        post.Summary = summary;
        post.Tags = tags;
        post.SeriesId = input.SeriesId;
        post.ContentMarkdown = input.ContentMarkdown ?? "";
        post.ContentHtml = renderer.Render(post.ContentMarkdown);
        return null;
    }

    private static async Task<Results<Ok<AdminPostDto>, BadRequest<ApiErrorResponse>>> Create(
        AdminPostInput input, HttpContext http, ReelGuoDbContext db, PostRenderer renderer, MediaUrlSigner signer, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var post = new Post { Title = "", Status = PostStatus.Draft, CreatedAt = now, UpdatedAt = now };
        if (await ApplyAsync(post, input, db, renderer, ct) is { } error)
            return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_post", error));
        db.Posts.Add(post);
        await db.SaveChangesAsync(ct);
        db.AddAudit(now, AdminActor.From(http), "post.create", "post", post.Id.ToString(), new { post.Title });
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(post, signer));
    }

    private static async Task<Results<Ok<AdminPostDto>, NotFound<ApiErrorResponse>>> Get(long id, ReelGuoDbContext db, MediaUrlSigner signer, CancellationToken ct)
    {
        var post = await db.Posts.AsNoTracking().Include(p => p.Series).FirstOrDefaultAsync(p => p.Id == id, ct);
        return post is null ? TypedResults.NotFound(ApiErrorResponse.Of("not_found", "文章不存在")) : TypedResults.Ok(ToDto(post, signer));
    }

    private static async Task<Results<Ok<AdminPostDto>, NotFound<ApiErrorResponse>, BadRequest<ApiErrorResponse>>> Update(
        long id, AdminPostInput input, HttpContext http, ReelGuoDbContext db, PostRenderer renderer, MediaUrlSigner signer, TimeProvider time, CancellationToken ct)
    {
        var post = await db.Posts.Include(p => p.Series).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (post is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "文章不存在"));
        if (await ApplyAsync(post, input, db, renderer, ct) is { } error)
            return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_post", error));
        post.UpdatedAt = time.GetUtcNow();
        db.AddAudit(time, AdminActor.From(http), "post.update", "post", id.ToString(), new { post.Title });
        await db.SaveChangesAsync(ct);
        await db.Entry(post).Reference(p => p.Series).LoadAsync(ct);
        return TypedResults.Ok(ToDto(post, signer));
    }

    private static async Task<IResult> UploadImage(
        long id, HttpContext http, ReelGuoDbContext db, StorageBuckets storage, MediaUrlSigner signer, TimeProvider time, bool isCover, CancellationToken ct)
    {
        var post = await db.Posts.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (post is null) return ApiResults.Error(StatusCodes.Status404NotFound, "not_found", "文章不存在");

        var (buffer, ext, error) = await ImageUpload.ReadAsync(http, MaxImageBytes, ct);
        if (error is not null) return ApiResults.Error(StatusCodes.Status400BadRequest, "invalid_image", error);
        await using var _ = buffer!;

        var key = $"p/posts/{id}-{(isCover ? "cover" : "img")}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}{ext}";
        await storage.Media.PutAsync(key, buffer!, StorageKeys.ContentTypeFor(key), ct);
        if (isCover)
        {
            post.CoverKey = key;
            post.UpdatedAt = time.GetUtcNow();
            db.AddAudit(time, AdminActor.From(http), "post.cover", "post", id.ToString());
            await db.SaveChangesAsync(ct);
        }
        return TypedResults.Ok(new ImageUrlDto(signer.PublicUrl(key)));
    }

    private static async Task<Results<Ok<AdminPostDto>, NotFound<ApiErrorResponse>, BadRequest<ApiErrorResponse>>> Publish(
        long id, HttpContext http, ReelGuoDbContext db, MediaUrlSigner signer, TimeProvider time, CancellationToken ct)
    {
        var post = await db.Posts.Include(p => p.Series).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (post is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "文章不存在"));
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(post.ContentMarkdown)) problems.Add("正文不能为空");
        if (post.CoverKey is null) problems.Add("请上传封面");
        if (problems.Count > 0) return TypedResults.BadRequest(ApiErrorResponse.Of("publish_blocked", "还不能发布", problems));

        var now = time.GetUtcNow();
        post.Status = PostStatus.Published;
        post.PublishedAt ??= now;
        post.UpdatedAt = now;
        db.AddAudit(now, AdminActor.From(http), "post.publish", "post", id.ToString(), new { post.Title });
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(post, signer));
    }

    private static async Task<Results<NoContent, NotFound<ApiErrorResponse>>> Unpublish(long id, HttpContext http, ReelGuoDbContext db, TimeProvider time, CancellationToken ct)
    {
        var post = await db.Posts.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (post is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "文章不存在"));
        post.Status = PostStatus.Hidden;
        post.UpdatedAt = time.GetUtcNow();
        db.AddAudit(time, AdminActor.From(http), "post.unpublish", "post", id.ToString(), new { post.Title });
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound<ApiErrorResponse>>> Delete(long id, HttpContext http, ReelGuoDbContext db, TimeProvider time, CancellationToken ct)
    {
        var post = await db.Posts.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (post is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "文章不存在"));
        db.Posts.Remove(post);
        db.AddAudit(time, AdminActor.From(http), "post.delete", "post", id.ToString(), new { post.Title });
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<List<AdminRankRow>>, NotFound<ApiErrorResponse>>> ListRanks(string kind, ReelGuoDbContext db, CancellationToken ct)
    {
        if (!Enum.TryParse<RankKind>(kind, ignoreCase: true, out var k)) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "榜单不存在"));
        return TypedResults.Ok(await db.Ranks.AsNoTracking().Where(r => r.Kind == k).OrderBy(r => r.Position)
            .Select(r => new AdminRankRow(r.Position, r.SeriesId, r.Series.Title, r.MetricValue, r.ComputedAt)).ToListAsync(ct));
    }
}

/// <summary>Reads a raw-body image upload, checking its real type from the first bytes.</summary>
public static class ImageUpload
{
    public static async Task<(MemoryStream? Buffer, string? Extension, string? Error)> ReadAsync(HttpContext http, long maxBytes, CancellationToken ct)
    {
        if (http.Request.ContentLength is { } len && len > maxBytes) return (null, null, $"图片不能超过 {maxBytes / 1024 / 1024} MB");
        var buffer = new MemoryStream();
        await http.Request.Body.CopyToAsync(buffer, ct);
        if (buffer.Length == 0 || buffer.Length > maxBytes)
        {
            await buffer.DisposeAsync();
            return (null, null, "图片为空或过大");
        }
        var ext = Sniff(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        if (ext is null)
        {
            await buffer.DisposeAsync();
            return (null, null, "图片需为 JPEG、PNG 或 WebP");
        }
        buffer.Position = 0;
        return (buffer, ext, null);
    }

    public static string? Sniff(ReadOnlySpan<byte> b) =>
        b.Length > 12 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? ".jpg"
        : b.Length > 12 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 ? ".png"
        : b.Length > 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8) ? ".webp"
        : null;
}
