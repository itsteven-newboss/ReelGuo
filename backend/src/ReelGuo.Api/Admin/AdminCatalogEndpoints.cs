using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Api.Media;
using ReelGuo.Data;
using ReelGuo.Data.Catalog;
using ReelGuo.Data.Entities;
using ReelGuo.Data.Jobs;
using ReelGuo.Data.Media;
using ReelGuo.Data.Storage;

namespace ReelGuo.Api.Admin;

public sealed record AdminCategoryDto(int Id, string Slug, string Name, string? Description, CategoryStatus Status, int SortOrder, int SeriesCount);

public sealed record UpdateCategoryRequest(string Name, string? Description, CategoryStatus Status, int SortOrder);

public sealed record SeriesInput(
    string CategorySlug,
    string Title,
    string? Description,
    List<string>? Tags,
    double Score,
    bool IsFinished,
    bool IsOriginal,
    int TotalEpisodes,
    string? AuthorName,
    string? ExternalId,
    string? RightsHolder,
    string? ContractRef,
    DateOnly? LicenceStart,
    DateOnly? LicenceEnd,
    List<string>? Territories,
    bool? AiGenerated,
    bool? HasRealPerson,
    string? ConsentRef,
    /// <summary>Editor pick weight for 推荐榜 (0-100; 0 = not picked).</summary>
    int RecommendWeight = 0,
    /// <summary>Coins per paid episode (0 = the whole series is free).</summary>
    int EpisodePrice = 0,
    /// <summary>Leading episodes that are always free.</summary>
    int FreeEpisodes = Pricing.DefaultFreeEpisodes);

public sealed record AdminEpisodeDto(
    long Id, int EpisodeNumber, string Title, EpisodeStatus Status, int Progress, string? Error,
    double Duration, int Width, int Height, string? PosterUrl, bool IsFree, bool HasSource, DateTimeOffset UpdatedAt);

public sealed record AdminSeriesDto(
    long Id, string CategorySlug, string Title, string Description, List<string> Tags, double Score,
    bool IsFinished, bool IsOriginal, int TotalEpisodes, string? AuthorName, string? ExternalId, string? CoverUrl,
    SeriesStatus Status, DateTimeOffset? PublishedAt,
    string? RightsHolder, string? ContractRef, DateOnly? LicenceStart, DateOnly? LicenceEnd, List<string> Territories,
    bool? AiGenerated, bool? HasRealPerson, string? ConsentRef, int RecommendWeight, int EpisodePrice, int FreeEpisodes,
    List<AdminEpisodeDto> Episodes, List<string> PublishProblems, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record AdminSeriesListItem(
    long Id, string Title, string CategorySlug, string CategoryName, SeriesStatus Status, int ReadyEpisodes, int EpisodeRows,
    int Processing, int Failed, string? CoverUrl, DateTimeOffset UpdatedAt);

public sealed record AdminSeriesPage(List<AdminSeriesListItem> Items, int Page, int Size, int Total);

public sealed record UploadTicket(string Method, string Url, Dictionary<string, string> Headers, string Key);

public sealed record CreateEpisodeRequest(int? EpisodeNumber, string? Title, string FileName, string? ContentType);

public sealed record ReplaceSourceRequest(string FileName, string? ContentType);

public sealed record EpisodeUploadResponse(AdminEpisodeDto Episode, UploadTicket Upload);

public sealed record UpdateEpisodeRequest(string? Title, bool IsFree);

public sealed record CoverResponse(string CoverUrl);

public static class AdminCatalogEndpoints
{
    private const long MaxCoverBytes = 5 * 1024 * 1024;
    private const long MaxUploadBytes = 10L * 1024 * 1024 * 1024;
    private static readonly TimeSpan UploadUrlLifetime = TimeSpan.FromHours(6);

    public static RouteGroupBuilder MapAdminCatalog(this RouteGroupBuilder admin)
    {
        var categories = admin.MapGroup("/categories").WithTags("Admin: catalog");
        categories.MapGet("/", ListCategories).WithSummary("All channels, including hidden and coming-soon ones.");
        categories.MapPut("/{slug}", UpdateCategory).WithSummary("Renames, reorders, hides or opens a channel.");

        var series = admin.MapGroup("/series").WithTags("Admin: catalog");
        series.MapGet("/", ListSeries).WithSummary("Series list with processing counts.");
        series.MapPost("/", CreateSeries).WithSummary("Creates a draft series.");
        series.MapGet("/{id:long}", GetSeries).WithSummary("Series with episodes and outstanding publish problems.");
        series.MapPut("/{id:long}", UpdateSeries).WithSummary("Updates series metadata and rights data.");
        series.MapPost("/{id:long}/cover", UploadCover).WithSummary("Uploads the cover image (JPEG, PNG or WebP, up to 5 MB) as the raw request body.");
        series.MapPost("/{id:long}/publish", Publish).WithSummary("Publishes when every publish rule passes; otherwise 400 with the problems listed.");
        series.MapPost("/{id:long}/unpublish", Unpublish).WithSummary("Hides a published series.");
        series.MapPost("/{id:long}/episodes", CreateEpisode).WithSummary("Creates an episode and returns where to upload its video.");

        var episodes = admin.MapGroup("/episodes").WithTags("Admin: catalog");
        episodes.MapPut("/{id:long}", UpdateEpisode).WithSummary("Updates an episode's title, and whether it is free even in a paid series.");
        episodes.MapPost("/{id:long}/upload", ReplaceSource).WithSummary("Starts replacing an episode's video.");
        episodes.MapPost("/{id:long}/uploaded", MarkUploaded).WithSummary("Confirms the upload finished and queues processing.");
        episodes.MapPost("/{id:long}/retry", Retry).WithSummary("Queues processing again after a failure.");
        episodes.MapDelete("/{id:long}", DeleteEpisode).WithSummary("Deletes an episode and its packaged media.");

        // Filesystem storage cannot sign upload URLs; tickets then point here instead.
        admin.MapPut("/uploads/raw/{**key}", UploadRaw).WithTags("Admin: catalog")
            .WithSummary("Receives an upload when storage cannot accept direct browser uploads (development).");

        admin.MapAdminImport();
        return admin;
    }

    // ---------- categories ----------

    private static async Task<Ok<List<AdminCategoryDto>>> ListCategories(ReelGuoDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.Categories.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
            .Select(c => new AdminCategoryDto(c.Id, c.Slug, c.Name, c.Description, c.Status, c.SortOrder, c.Series.Count))
            .ToListAsync(ct));

    private static async Task<Results<Ok<AdminCategoryDto>, NotFound<ApiErrorResponse>, BadRequest<ApiErrorResponse>>> UpdateCategory(
        string slug, UpdateCategoryRequest request, HttpContext http, ReelGuoDbContext db, TimeProvider time, CancellationToken ct)
    {
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Slug == slug, ct);
        if (category is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "频道不存在"));
        var name = (request.Name ?? "").Trim();
        if (name.Length is 0 or > 64) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_name", "频道名称需为 1-64 个字符"));

        var before = new { category.Name, category.Description, status = category.Status.ToString(), category.SortOrder };
        category.Name = name;
        category.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        category.Status = request.Status;
        category.SortOrder = request.SortOrder;
        category.UpdatedAt = time.GetUtcNow();
        db.AddAudit(time, AdminActor.From(http), "category.update", "category", slug,
            new { before, after = new { name, category.Description, status = request.Status.ToString(), request.SortOrder } });
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new AdminCategoryDto(category.Id, category.Slug, category.Name, category.Description, category.Status, category.SortOrder,
            await db.Series.CountAsync(s => s.CategoryId == category.Id, ct)));
    }

    // ---------- series ----------

    private static async Task<Ok<AdminSeriesPage>> ListSeries(
        ReelGuoDbContext db, MediaUrlSigner signer,
        [FromQuery] string? q, [FromQuery] string? status, [FromQuery] int? page, [FromQuery] int? size, CancellationToken ct)
    {
        // Accept the JSON spelling ("published") as well as the enum name.
        SeriesStatus? statusFilter = Enum.TryParse<SeriesStatus>((status ?? "").Replace("_", ""), ignoreCase: true, out var parsed) ? parsed : null;
        var p = Math.Max(1, page ?? 1);
        var s = Math.Clamp(size ?? 20, 1, 100);
        var query = db.Series.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(x => EF.Functions.ILike(x.Title, $"%{q.Trim()}%") || x.ExternalId == q.Trim());
        if (statusFilter is not null) query = query.Where(x => x.Status == statusFilter);

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.UpdatedAt).Skip((p - 1) * s).Take(s)
            .Select(x => new
            {
                x.Id, x.Title, x.Category.Slug, CategoryName = x.Category.Name, x.Status, x.CoverKey, x.UpdatedAt,
                Ready = x.Episodes.Count(e => e.Status == EpisodeStatus.Ready),
                Rows = x.Episodes.Count,
                Processing = x.Episodes.Count(e => e.Status == EpisodeStatus.Processing || e.Status == EpisodeStatus.Uploading),
                Failed = x.Episodes.Count(e => e.Status == EpisodeStatus.Failed),
            })
            .ToListAsync(ct);

        var items = rows.Select(x => new AdminSeriesListItem(x.Id, x.Title, x.Slug, x.CategoryName, x.Status, x.Ready, x.Rows,
            x.Processing, x.Failed, signer.PublicUrlOrNull(x.CoverKey), x.UpdatedAt)).ToList();
        return TypedResults.Ok(new AdminSeriesPage(items, p, s, total));
    }

    private static async Task<Results<Ok<AdminSeriesDto>, BadRequest<ApiErrorResponse>, Conflict<ApiErrorResponse>>> CreateSeries(
        SeriesInput input, HttpContext http, ReelGuoDbContext db, MediaUrlSigner signer, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var series = new Series { Title = "", Status = SeriesStatus.Draft, CreatedAt = now, UpdatedAt = now };
        var error = await ApplyInputAsync(series, input, db, now, ct);
        if (error is not null) return error.Value.Conflict
            ? TypedResults.Conflict(error.Value.Body)
            : TypedResults.BadRequest(error.Value.Body);

        db.Series.Add(series);
        await db.SaveChangesAsync(ct);
        db.AddAudit(time, AdminActor.From(http), "series.create", "series", series.Id.ToString(), new { series.Title });
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await LoadDtoAsync(db, signer, time, series.Id, ct) ?? throw new InvalidOperationException());
    }

    private static async Task<Results<Ok<AdminSeriesDto>, NotFound<ApiErrorResponse>>> GetSeries(
        long id, ReelGuoDbContext db, MediaUrlSigner signer, TimeProvider time, CancellationToken ct)
    {
        var dto = await LoadDtoAsync(db, signer, time, id, ct);
        return dto is null ? TypedResults.NotFound(ApiErrorResponse.Of("not_found", "剧集不存在")) : TypedResults.Ok(dto);
    }

    private static async Task<Results<Ok<AdminSeriesDto>, NotFound<ApiErrorResponse>, BadRequest<ApiErrorResponse>, Conflict<ApiErrorResponse>>> UpdateSeries(
        long id, SeriesInput input, HttpContext http, ReelGuoDbContext db, MediaUrlSigner signer, TimeProvider time, CancellationToken ct)
    {
        var series = await db.Series.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (series is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "剧集不存在"));

        var now = time.GetUtcNow();
        var error = await ApplyInputAsync(series, input, db, now, ct);
        if (error is not null) return error.Value.Conflict
            ? TypedResults.Conflict(error.Value.Body)
            : TypedResults.BadRequest(error.Value.Body);

        series.UpdatedAt = now;
        db.AddAudit(now, AdminActor.From(http), "series.update", "series", id.ToString(), new { series.Title, series.EpisodePrice, series.FreeEpisodes });
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok((await LoadDtoAsync(db, signer, time, id, ct))!);
    }

    private static async Task<(ApiErrorResponse Body, bool Conflict)?> ApplyInputAsync(
        Series series, SeriesInput input, ReelGuoDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        static (ApiErrorResponse, bool) Bad(string code, string message) => (ApiErrorResponse.Of(code, message), false);

        var title = (input.Title ?? "").Trim();
        if (title.Length is 0 or > 200) return Bad("invalid_title", "剧名需为 1-200 个字符");

        var category = await db.Categories.FirstOrDefaultAsync(c => c.Slug == input.CategorySlug, ct);
        if (category is null) return Bad("invalid_category", "频道不存在");
        if (category.Status != CategoryStatus.Active && category.Id != series.CategoryId)
            return Bad("category_not_open", $"频道「{category.Name}」未开放，不能添加内容");

        if (input.Score is < 0 or > 10) return Bad("invalid_score", "评分需在 0-10 之间");
        if (input.TotalEpisodes is < 0 or > 10000) return Bad("invalid_total", "总集数无效");
        if (input.RecommendWeight is < 0 or > 100) return Bad("invalid_recommend_weight", "推荐权重需在 0-100 之间");
        if (input.EpisodePrice is < 0 or > Pricing.MaxEpisodePrice) return Bad("invalid_price", $"单集价格需在 0-{Pricing.MaxEpisodePrice} 金币之间");
        if (input.FreeEpisodes is < 0 or > 10000) return Bad("invalid_free_episodes", "免费集数无效");

        var tags = (input.Tags ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList();
        if (tags.Count > 10 || tags.Any(t => t.Length > 20)) return Bad("invalid_tags", "最多 10 个标签，每个不超过 20 个字符");

        var territories = PublishRules.NormalizeTerritories(input.Territories ?? []);
        if (territories is null) return Bad("invalid_territories", "授权地区需为 ISO 国家代码（如 US、JP）或 *（全球）");

        if (input.LicenceStart is { } start && input.LicenceEnd is { } end && end < start)
            return Bad("invalid_licence_dates", "授权结束日期早于开始日期");

        var externalId = string.IsNullOrWhiteSpace(input.ExternalId) ? null : input.ExternalId.Trim();
        if (externalId is not null && await db.Series.AnyAsync(s => s.ExternalId == externalId && s.Id != series.Id, ct))
            return (ApiErrorResponse.Of("external_id_taken", "外部编号已被其他剧集使用"), true);

        series.CategoryId = category.Id;
        series.Title = title;
        series.Description = (input.Description ?? "").Trim();
        series.Tags = tags;
        series.Score = Math.Round(input.Score, 1);
        series.IsFinished = input.IsFinished;
        series.IsOriginal = input.IsOriginal;
        series.TotalEpisodes = input.TotalEpisodes;
        series.ExternalId = externalId;
        series.RightsHolder = Clean(input.RightsHolder);
        series.ContractRef = Clean(input.ContractRef);
        series.LicenceStart = input.LicenceStart;
        series.LicenceEnd = input.LicenceEnd;
        series.Territories = territories;
        series.AiGenerated = input.AiGenerated;
        series.HasRealPerson = input.HasRealPerson;
        series.ConsentRef = Clean(input.ConsentRef);
        series.RecommendWeight = input.RecommendWeight;
        series.EpisodePrice = input.EpisodePrice;
        series.FreeEpisodes = input.FreeEpisodes;
        series.Author = string.IsNullOrWhiteSpace(input.AuthorName) ? null : await SeriesStats.FindOrCreateAuthorAsync(db, input.AuthorName, now, ct);
        if (series.Author is null) series.AuthorId = null;
        return null;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task<AdminSeriesDto?> LoadDtoAsync(ReelGuoDbContext db, MediaUrlSigner signer, TimeProvider time, long id, CancellationToken ct)
    {
        var s = await db.Series.AsNoTracking().Include(x => x.Category).Include(x => x.Author)
            .Include(x => x.Episodes.OrderBy(e => e.EpisodeNumber))
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return null;

        var ready = s.Episodes.Count(e => e.Status == EpisodeStatus.Ready);
        var problems = s.Status == SeriesStatus.Published
            ? []
            : PublishRules.Check(s, s.Category, ready, DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime));
        return new AdminSeriesDto(
            s.Id, s.Category.Slug, s.Title, s.Description, s.Tags, s.Score, s.IsFinished, s.IsOriginal, s.TotalEpisodes,
            s.Author?.Nickname, s.ExternalId, signer.PublicUrlOrNull(s.CoverKey), s.Status, s.PublishedAt,
            s.RightsHolder, s.ContractRef, s.LicenceStart, s.LicenceEnd, s.Territories, s.AiGenerated, s.HasRealPerson, s.ConsentRef,
            s.RecommendWeight, s.EpisodePrice, s.FreeEpisodes, s.Episodes.Select(e => ToDto(e, signer)).ToList(), problems, s.CreatedAt, s.UpdatedAt);
    }

    internal static AdminEpisodeDto ToDto(Episode e, MediaUrlSigner signer) => new(
        e.Id, e.EpisodeNumber, e.Title, e.Status, e.ProcessingProgress, e.ProcessingError, e.DurationSeconds,
        e.Width, e.Height, signer.PublicUrlOrNull(e.PosterKey), e.IsFree, e.SourceKey is not null, e.UpdatedAt);

    private static async Task<Results<Ok<CoverResponse>, NotFound<ApiErrorResponse>, BadRequest<ApiErrorResponse>>> UploadCover(
        long id, HttpContext http, ReelGuoDbContext db, StorageBuckets storage, MediaUrlSigner signer, TimeProvider time, CancellationToken ct)
    {
        var series = await db.Series.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (series is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "剧集不存在"));
        if (http.Request.ContentLength is > MaxCoverBytes)
            return TypedResults.BadRequest(ApiErrorResponse.Of("cover_too_large", "封面不能超过 5 MB"));

        using var buffer = new MemoryStream();
        await http.Request.Body.CopyToAsync(buffer, ct);
        if (buffer.Length is 0 or > MaxCoverBytes)
            return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_cover", "封面为空或超过 5 MB"));

        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        var ext = ImageExtension(bytes);
        if (ext is null) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_cover", "封面需为 JPEG、PNG 或 WebP 图片"));

        var key = MediaPaths.CoverKey(id, ext);
        buffer.Position = 0;
        await storage.Media.PutAsync(key, buffer, StorageKeys.ContentTypeFor(key), ct);
        series.CoverKey = key;
        series.UpdatedAt = time.GetUtcNow();
        db.AddAudit(time, AdminActor.From(http), "series.cover", "series", id.ToString());
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new CoverResponse(signer.PublicUrl(key)));
    }

    /// <summary>Identifies the image type from its first bytes rather than trusting the file name.</summary>
    private static string? ImageExtension(ReadOnlySpan<byte> b) =>
        b.Length > 12 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? ".jpg"
        : b.Length > 12 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 ? ".png"
        : b.Length > 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8) ? ".webp"
        : null;

    private static async Task<Results<Ok<AdminSeriesDto>, NotFound<ApiErrorResponse>, BadRequest<ApiErrorResponse>>> Publish(
        long id, HttpContext http, ReelGuoDbContext db, MediaUrlSigner signer, IRevokedSeriesStore revoked, TimeProvider time, CancellationToken ct)
    {
        var series = await db.Series.Include(s => s.Category).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (series is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "剧集不存在"));

        var now = time.GetUtcNow();
        var ready = await db.Episodes.CountAsync(e => e.SeriesId == id && e.Status == EpisodeStatus.Ready, ct);
        var problems = PublishRules.Check(series, series.Category, ready, DateOnly.FromDateTime(now.UtcDateTime));
        if (problems.Count > 0)
            return TypedResults.BadRequest(ApiErrorResponse.Of("publish_blocked", "还不能发布，请先处理以下问题", problems));

        series.Status = SeriesStatus.Published;
        series.PublishedAt ??= now;
        series.UpdatedAt = now;
        db.AddAudit(now, AdminActor.From(http), "series.publish", "series", id.ToString(), new { series.Title });
        await db.SaveChangesAsync(ct);
        revoked.Invalidate();
        return TypedResults.Ok((await LoadDtoAsync(db, signer, time, id, ct))!);
    }

    private static async Task<Results<NoContent, NotFound<ApiErrorResponse>>> Unpublish(
        long id, HttpContext http, ReelGuoDbContext db, IRevokedSeriesStore revoked, TimeProvider time, CancellationToken ct)
    {
        var series = await db.Series.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (series is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "剧集不存在"));
        series.Status = SeriesStatus.Hidden;
        series.UpdatedAt = time.GetUtcNow();
        db.AddAudit(time, AdminActor.From(http), "series.unpublish", "series", id.ToString(), new { series.Title });
        await db.SaveChangesAsync(ct);
        // Links already handed out stop playing too (this instance at once; CDN edges within a minute).
        revoked.Invalidate();
        return TypedResults.NoContent();
    }

    // ---------- episodes ----------

    private static UploadTicket Ticket(StorageBuckets storage, string key, string contentType)
    {
        var direct = storage.Raw.CreateUploadUrl(key, contentType, UploadUrlLifetime);
        return direct is not null
            ? new UploadTicket("PUT", direct.ToString(), new() { ["Content-Type"] = contentType }, key)
            : new UploadTicket("PUT", $"/api/admin/uploads/raw/{key}", new() { ["Content-Type"] = contentType, [AdminAuth.CsrfHeader] = "1" }, key);
    }

    internal static UploadTicket TicketFor(StorageBuckets storage, string key, string? contentType) =>
        Ticket(storage, key, string.IsNullOrWhiteSpace(contentType) ? StorageKeys.ContentTypeFor(key) : contentType);

    private static string SourceKeyFor(long seriesId, string fileName) =>
        $"uploads/{seriesId}/{Guid.NewGuid():N}/{StorageKeys.SanitizeFileName(fileName)}";

    private static async Task<Results<Ok<EpisodeUploadResponse>, NotFound<ApiErrorResponse>, BadRequest<ApiErrorResponse>, Conflict<ApiErrorResponse>>> CreateEpisode(
        long id, CreateEpisodeRequest request, HttpContext http, ReelGuoDbContext db, StorageBuckets storage, MediaUrlSigner signer,
        TimeProvider time, CancellationToken ct)
    {
        var series = await db.Series.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (series is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "剧集不存在"));
        if (string.IsNullOrWhiteSpace(request.FileName))
            return TypedResults.BadRequest(ApiErrorResponse.Of("file_required", "请选择视频文件"));

        var number = request.EpisodeNumber ?? (await db.Episodes.Where(e => e.SeriesId == id).MaxAsync(e => (int?)e.EpisodeNumber, ct) ?? 0) + 1;
        if (number < 1) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_episode_number", "集数需从 1 开始"));
        if (await db.Episodes.AnyAsync(e => e.SeriesId == id && e.EpisodeNumber == number, ct))
            return TypedResults.Conflict(ApiErrorResponse.Of("episode_exists", $"第 {number} 集已存在"));

        var now = time.GetUtcNow();
        var key = SourceKeyFor(id, request.FileName);
        var episode = new Episode
        {
            SeriesId = id,
            EpisodeNumber = number,
            Title = (request.Title ?? "").Trim(),
            Status = EpisodeStatus.Uploading,
            SourceKey = key,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Episodes.Add(episode);
        db.AddAudit(now, AdminActor.From(http), "episode.create", "series", id.ToString(), new { episode = number });
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new EpisodeUploadResponse(ToDto(episode, signer), TicketFor(storage, key, request.ContentType)));
    }

    private static async Task<Results<Ok<AdminEpisodeDto>, NotFound<ApiErrorResponse>, BadRequest<ApiErrorResponse>>> UpdateEpisode(
        long id, UpdateEpisodeRequest request, HttpContext http, ReelGuoDbContext db, MediaUrlSigner signer, TimeProvider time, CancellationToken ct)
    {
        var episode = await db.Episodes.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (episode is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "分集不存在"));
        var title = (request.Title ?? "").Trim();
        if (title.Length > 200) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_title", "标题不能超过 200 个字符"));
        episode.Title = title;
        episode.IsFree = request.IsFree;
        episode.UpdatedAt = time.GetUtcNow();
        db.AddAudit(time, AdminActor.From(http), "episode.update", "episode", id.ToString(), new { title, request.IsFree });
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(episode, signer));
    }

    private static async Task<Results<Ok<EpisodeUploadResponse>, NotFound<ApiErrorResponse>, BadRequest<ApiErrorResponse>>> ReplaceSource(
        long id, ReplaceSourceRequest request, HttpContext http, ReelGuoDbContext db, StorageBuckets storage, MediaUrlSigner signer,
        TimeProvider time, CancellationToken ct)
    {
        var episode = await db.Episodes.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (episode is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "分集不存在"));
        if (string.IsNullOrWhiteSpace(request.FileName))
            return TypedResults.BadRequest(ApiErrorResponse.Of("file_required", "请选择视频文件"));

        // A ready episode keeps playing its current media until the new upload finishes processing.
        episode.SourceKey = SourceKeyFor(episode.SeriesId, request.FileName);
        if (episode.Status != EpisodeStatus.Ready) episode.Status = EpisodeStatus.Uploading;
        episode.UpdatedAt = time.GetUtcNow();
        db.AddAudit(time, AdminActor.From(http), "episode.replace_source", "episode", id.ToString());
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new EpisodeUploadResponse(ToDto(episode, signer), TicketFor(storage, episode.SourceKey, request.ContentType)));
    }

    private static async Task<Results<Ok<AdminEpisodeDto>, NotFound<ApiErrorResponse>, BadRequest<ApiErrorResponse>>> MarkUploaded(
        long id, HttpContext http, ReelGuoDbContext db, StorageBuckets storage, MediaJobQueue queue, MediaUrlSigner signer,
        TimeProvider time, CancellationToken ct)
    {
        var episode = await db.Episodes.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (episode is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "分集不存在"));
        if (episode.SourceKey is null || !await storage.Raw.ExistsAsync(episode.SourceKey, ct))
            return TypedResults.BadRequest(ApiErrorResponse.Of("source_missing", "没有找到上传的视频文件，请重新上传"));

        if (episode.Status != EpisodeStatus.Ready) episode.Status = EpisodeStatus.Processing;
        episode.ProcessingProgress = 0;
        episode.ProcessingError = null;
        episode.UpdatedAt = time.GetUtcNow();
        await queue.EnqueueTranscodeAsync(id, ct);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(episode, signer));
    }

    private static async Task<Results<Ok<AdminEpisodeDto>, NotFound<ApiErrorResponse>, BadRequest<ApiErrorResponse>>> Retry(
        long id, HttpContext http, ReelGuoDbContext db, MediaJobQueue queue, MediaUrlSigner signer, TimeProvider time, CancellationToken ct)
    {
        var episode = await db.Episodes.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (episode is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "分集不存在"));
        if (episode.SourceKey is null) return TypedResults.BadRequest(ApiErrorResponse.Of("source_missing", "该分集还没有上传视频"));

        if (episode.Status != EpisodeStatus.Ready) episode.Status = EpisodeStatus.Processing;
        episode.ProcessingProgress = 0;
        episode.ProcessingError = null;
        episode.UpdatedAt = time.GetUtcNow();
        await queue.EnqueueTranscodeAsync(id, ct);
        db.AddAudit(time, AdminActor.From(http), "episode.retry", "episode", id.ToString());
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(episode, signer));
    }

    private static async Task<Results<NoContent, NotFound<ApiErrorResponse>>> DeleteEpisode(
        long id, HttpContext http, ReelGuoDbContext db, StorageBuckets storage, TimeProvider time, ILoggerFactory logs, CancellationToken ct)
    {
        var episode = await db.Episodes.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (episode is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "分集不存在"));

        db.Episodes.Remove(episode);
        db.AddAudit(time, AdminActor.From(http), "episode.delete", "series", episode.SeriesId.ToString(), new { episode = episode.EpisodeNumber });
        await db.SaveChangesAsync(ct);
        await SeriesStats.RefreshAsync(db, episode.SeriesId, ct);

        try
        {
            await storage.Media.DeletePrefixAsync($"s/{episode.SeriesId}/e/{episode.EpisodeNumber}/", ct);
        }
        catch (Exception ex)
        {
            logs.CreateLogger("AdminCatalog").LogWarning(ex, "Could not delete media for episode {EpisodeId}", id);
        }
        return TypedResults.NoContent();
    }

    // ---------- uploads (filesystem storage only) ----------

    private static async Task<Results<NoContent, BadRequest<ApiErrorResponse>>> UploadRaw(
        string key, HttpContext http, StorageBuckets storage, CancellationToken ct)
    {
        if (!StorageKeys.IsValid(key) || !(key.StartsWith("uploads/", StringComparison.Ordinal) || key.StartsWith(StorageKeys.IncomingPrefix, StringComparison.Ordinal)))
            return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_key", "无效的上传路径"));

        var sizeLimit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeLimit is { IsReadOnly: false }) sizeLimit.MaxRequestBodySize = MaxUploadBytes;

        await storage.Raw.PutAsync(key, http.Request.Body, http.Request.ContentType ?? StorageKeys.ContentTypeFor(key), ct);
        return TypedResults.NoContent();
    }
}
