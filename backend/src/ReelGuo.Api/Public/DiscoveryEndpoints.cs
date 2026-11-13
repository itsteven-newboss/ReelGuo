using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelGuo.Api.Geo;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Data;
using ReelGuo.Data.Entities;
using ReelGuo.Data.Media;
using ReelGuo.Data.Telemetry;

namespace ReelGuo.Api.Public;

public sealed record PlayEventInput(string Type, long SeriesId, int Ep);

/// <summary>How one play went, sent when the viewer leaves an episode (startup time, stalls, errors).</summary>
public sealed record PlaybackSampleInput(
    long SeriesId, int Ep, int? StartupMs, int RebufferCount, int RebufferMs, double WatchedS, int? MaxHeight, string? Error,
    PlaybackDevice Device, string Browser);

public sealed record PlayEventBatch(string SessionId, List<PlayEventInput> Events, List<PlaybackSampleInput>? Samples);

public sealed record RankMetricsDto(long Collect, long Comment, long Like, long Play);

public sealed record RankItemDto(
    int Rank, long VideoId, string Title, string? Cover, List<string> Tags, double Score, string CateName,
    int EpisodeCount, bool IsFinished, string MetricLabel, double MetricValue, RankMetricsDto Metrics, string Description = "");

public sealed record RankListDto(string Type, DateTimeOffset? UpdatedAt, List<RankItemDto> Items);

public sealed record SearchResultDto(List<VideoCardDto> Items, PaginationDto Pagination, string Keyword);

public sealed record SearchSuggestDto(List<string> HotTags, List<VideoCardDto> Recommended);

public sealed record PostCardDto(long Id, string Title, string Summary, string? Cover, List<string> Tags, long Hot, long? VideoId, DateTimeOffset? PublishedAt);

public sealed record PostCardPage(List<PostCardDto> Items, PaginationDto Pagination);

public sealed record PostDetailDto(
    long Id, string Title, string Summary, string? Cover, List<string> Tags, long Hot, string ContentHtml,
    VideoCardDto? Series, DateTimeOffset? PublishedAt, long LikeCount, long CollectCount, long CommentCount);

public static partial class DiscoveryEndpoints
{
    public const int MaxEventsPerBatch = 50;
    public const int MaxSamplesPerBatch = 20;
    private static readonly HashSet<string> Browsers = ["chrome", "safari", "firefox", "edge", "samsung", "other"];

    /// <summary>Fallback genre chips when the catalog is too small to compute popular tags.</summary>
    private static readonly string[] FallbackTags = ["都市", "逆袭", "古风", "重生", "豪门", "校园", "仙侠", "职场", "奇幻", "脑洞", "热血", "甜宠"];

    public static RouteGroupBuilder MapDiscovery(this RouteGroupBuilder v1)
    {
        v1.MapPost("/events", Events).WithTags("Playback").RequireRateLimiting(RateLimits.Events)
            .WithSummary("Anonymous playback signals: start / complete for rankings, plus playback-quality samples. Batched.");
        v1.MapGet("/ranks/{kind}", Ranks).WithTags("Discovery")
            .WithSummary("热播榜 (hot) / 推荐榜 (recommend) / 潜力榜 (potential), recomputed every 10 minutes.");
        v1.MapGet("/search", Search).WithTags("Discovery")
            .WithSummary("Searches titles, tags, descriptions and authors.");
        v1.MapGet("/search/suggest", Suggest).WithTags("Discovery")
            .WithSummary("Popular tags and a rotating set of recommendations for the search page.");
        v1.MapGet("/posts", ListPosts).WithTags("Posts").WithSummary("Published 吃瓜 posts, newest first.");
        v1.MapGet("/posts/{id:long}", GetPost).WithTags("Posts").WithSummary("One post with its sanitised HTML.");
        return v1;
    }

    // ---------- events ----------

    private static async Task<Results<NoContent, BadRequest<ApiErrorResponse>>> Events(
        PlayEventBatch batch, HttpContext http, ReelGuoDbContext db, ICountryResolver resolver, TimeProvider time, CancellationToken ct)
    {
        if (!SessionIdPattern().IsMatch(batch.SessionId ?? ""))
            return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_session", "无效的会话标识"));
        var events = batch.Events ?? [];
        var samples = batch.Samples ?? [];
        if (events.Count > MaxEventsPerBatch || samples.Count > MaxSamplesPerBatch || events.Count + samples.Count == 0)
            return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_events", $"每次 1-{MaxEventsPerBatch} 条事件，最多 {MaxSamplesPerBatch} 条播放质量记录"));

        var parsed = events
            .Select(e => (Kind: e.Type switch { "start" => (PlayEventKind?)PlayEventKind.Start, "complete" => PlayEventKind.Complete, _ => null }, e.SeriesId, e.Ep))
            .Where(e => e.Kind is not null && e.Ep is > 0 and < 100_000)
            .ToList();
        var goodSamples = samples.Where(s => s.Ep is > 0 and < 100_000 && double.IsFinite(s.WatchedS)).ToList();
        if (parsed.Count == 0 && goodSamples.Count == 0) return TypedResults.NoContent();

        // Only published series count; unknown ids are dropped silently.
        var ids = parsed.Select(e => e.SeriesId).Concat(goodSamples.Select(s => s.SeriesId)).Distinct().ToList();
        var known = (await db.Series.Where(s => ids.Contains(s.Id) && s.Status == SeriesStatus.Published).Select(s => s.Id).ToListAsync(ct)).ToHashSet();
        var now = time.GetUtcNow();
        var country = resolver.Resolve(http);
        foreach (var e in parsed.Where(e => known.Contains(e.SeriesId)))
        {
            db.PlayEvents.Add(new PlayEvent { SeriesId = e.SeriesId, EpisodeNumber = e.Ep, Kind = e.Kind!.Value, SessionId = batch.SessionId!, Country = country, At = now });
            ReelGuoTelemetry.PlayEvents.Add(1, new KeyValuePair<string, object?>("kind", e.Kind.Value.ToString().ToLowerInvariant()));
        }
        foreach (var s in goodSamples.Where(s => known.Contains(s.SeriesId)))
        {
            var error = s.Error is { Length: > 0 and <= 64 } code && ErrorCodePattern().IsMatch(code) ? code : null;
            db.PlaybackSamples.Add(new PlaybackSample
            {
                SessionId = batch.SessionId!, SeriesId = s.SeriesId, EpisodeNumber = s.Ep, Country = country, Device = s.Device,
                Browser = Browsers.Contains(s.Browser ?? "") ? s.Browser! : "other",
                StartupMs = s.StartupMs is { } ms ? Math.Clamp(ms, 0, 120_000) : null,
                RebufferCount = Math.Clamp(s.RebufferCount, 0, 10_000),
                RebufferMs = Math.Clamp(s.RebufferMs, 0, 3_600_000),
                WatchedSeconds = Math.Clamp(s.WatchedS, 0, 24 * 3600),
                MaxHeight = s.MaxHeight is { } h ? Math.Clamp(h, 0, 4320) : null,
                Error = error, At = now,
            });
            if (error is not null) ReelGuoTelemetry.PlaybackErrors.Add(1, new KeyValuePair<string, object?>("error", error));
        }
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex SessionIdPattern();

    [GeneratedRegex("^[A-Za-z0-9_.:-]+$")]
    private static partial Regex ErrorCodePattern();

    // ---------- ranks ----------

    private static readonly Dictionary<string, (RankKind Kind, string Label)> RankKinds = new()
    {
        ["hot"] = (RankKind.Hot, "热度"),
        ["recommend"] = (RankKind.Recommend, "推荐值"),
        ["potential"] = (RankKind.Potential, "潜力值"),
    };

    private static async Task<Results<Ok<RankListDto>, NotFound<ApiErrorResponse>>> Ranks(
        string kind, HttpContext http, ReelGuoDbContext db, ICountryResolver resolver, MediaUrlSigner signer, TimeProvider time, CancellationToken ct)
    {
        if (!RankKinds.TryGetValue(kind, out var k)) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "榜单不存在"));

        var visible = db.Series.AsNoTracking().VisibleTo(resolver.Resolve(http), PublicEndpoints.Today(time));
        var rows = await db.Ranks.AsNoTracking()
            .Where(r => r.Kind == k.Kind && visible.Any(s => s.Id == r.SeriesId))
            .OrderBy(r => r.Position)
            .Select(r => new
            {
                r.MetricValue, r.ComputedAt, r.Series.Id, r.Series.Title, r.Series.Description, r.Series.CoverKey, r.Series.Tags, r.Series.Score,
                Category = r.Series.Category.Name, r.Series.EpisodeCount, r.Series.IsFinished,
                r.Series.CollectCount, r.Series.CommentCount, r.Series.LikeCount, r.Series.PlayCount,
            })
            .ToListAsync(ct);

        var items = rows.Select((r, i) => new RankItemDto(
            i + 1, r.Id, r.Title, signer.PublicUrlOrNull(r.CoverKey), r.Tags, r.Score, r.Category, r.EpisodeCount, r.IsFinished,
            k.Label, r.MetricValue, new RankMetricsDto(r.CollectCount, r.CommentCount, r.LikeCount, r.PlayCount), r.Description)).ToList();
        return TypedResults.Ok(new RankListDto(kind, rows.FirstOrDefault()?.ComputedAt, items));
    }

    // ---------- search ----------

    private static async Task<Results<Ok<SearchResultDto>, BadRequest<ApiErrorResponse>>> Search(
        HttpContext http, ReelGuoDbContext db, ICountryResolver resolver, MediaUrlSigner signer, TimeProvider time,
        [FromQuery] string? q, [FromQuery] int? page, [FromQuery] int? size, CancellationToken ct)
    {
        var keyword = (q ?? "").Trim();
        if (keyword.Length is 0 or > 50) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_query", "请输入 1-50 个字的关键词"));

        var pattern = "%" + keyword.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
        var query = db.Series.AsNoTracking().VisibleTo(resolver.Resolve(http), PublicEndpoints.Today(time))
            .Where(s => EF.Functions.ILike(s.Title, pattern, @"\")
                || s.Tags.Contains(keyword)
                || EF.Functions.ILike(s.Description, pattern, @"\")
                || (s.Author != null && EF.Functions.ILike(s.Author.Nickname, pattern, @"\")))
            // Title matches first, then popularity.
            .OrderByDescending(s => EF.Functions.ILike(s.Title, pattern, @"\"))
            .ThenByDescending(s => s.HotScore)
            .ThenByDescending(s => s.PublishedAt);

        var result = await PublicEndpoints.PageAsync(query, signer, page, size ?? 20, ct);
        return TypedResults.Ok(new SearchResultDto(result.Items, result.Pagination, keyword));
    }

    private static async Task<Ok<SearchSuggestDto>> Suggest(
        HttpContext http, ReelGuoDbContext db, ICountryResolver resolver, MediaUrlSigner signer, TimeProvider time,
        [FromQuery] int? seed, CancellationToken ct)
    {
        var popular = await db.Series.AsNoTracking().VisibleTo(resolver.Resolve(http), PublicEndpoints.Today(time))
            .OrderByDescending(s => s.HotScore).ThenByDescending(s => s.PublishedAt)
            .Take(30).Include(s => s.Author).Include(s => s.Category).ToListAsync(ct);

        // Tags weighted by how popular their series are, padded with common genres.
        var tags = popular.SelectMany((s, i) => s.Tags.Select(t => (Tag: t, Weight: 30 - i)))
            .GroupBy(x => x.Tag).OrderByDescending(g => g.Sum(x => x.Weight)).Select(g => g.Key)
            .Concat(FallbackTags).Distinct().Take(12).ToList();

        // "换一批": a different deterministic sample for each seed.
        var rng = new Random(seed ?? 0);
        var recommended = popular.OrderBy(_ => rng.Next()).Take(6).Select(s => PublicEndpoints.ToCard(s, signer)).ToList();
        return TypedResults.Ok(new SearchSuggestDto(tags, recommended));
    }

    // ---------- posts ----------

    private static IQueryable<Post> PublishedPosts(ReelGuoDbContext db, TimeProvider time)
    {
        var now = time.GetUtcNow();
        return db.Posts.AsNoTracking().Where(p => p.Status == PostStatus.Published && p.PublishedAt <= now);
    }

    private static async Task<Ok<PostCardPage>> ListPosts(
        ReelGuoDbContext db, MediaUrlSigner signer, TimeProvider time, [FromQuery] int? page, [FromQuery] int? size, CancellationToken ct)
    {
        var p = Math.Max(1, page ?? 1);
        var s = Math.Clamp(size ?? 20, 1, 50);
        var query = PublishedPosts(db, time);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.PublishedAt).ThenByDescending(x => x.Id).Skip((p - 1) * s).Take(s).ToListAsync(ct);
        var items = rows.Select(x => new PostCardDto(x.Id, x.Title, x.Summary, signer.PublicUrlOrNull(x.CoverKey), x.Tags, x.ViewCount, x.SeriesId, x.PublishedAt)).ToList();
        return TypedResults.Ok(new PostCardPage(items, new PaginationDto(p, Math.Max(1, (int)Math.Ceiling(total / (double)s)), s, total)));
    }

    private static async Task<Results<Ok<PostDetailDto>, NotFound<ApiErrorResponse>>> GetPost(
        long id, HttpContext http, ReelGuoDbContext db, ICountryResolver resolver, MediaUrlSigner signer, TimeProvider time, CancellationToken ct)
    {
        var post = await PublishedPosts(db, time).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (post is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "内容不存在或已下架"));

        await db.Posts.Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.ViewCount, x => x.ViewCount + 1), ct);

        // The linked series is shown only if this visitor may watch it.
        VideoCardDto? series = null;
        if (post.SeriesId is { } sid)
        {
            var s = await db.Series.AsNoTracking().VisibleTo(resolver.Resolve(http), PublicEndpoints.Today(time))
                .Include(x => x.Author).Include(x => x.Category).FirstOrDefaultAsync(x => x.Id == sid, ct);
            if (s is not null) series = PublicEndpoints.ToCard(s, signer);
        }

        return TypedResults.Ok(new PostDetailDto(post.Id, post.Title, post.Summary, signer.PublicUrlOrNull(post.CoverKey), post.Tags,
            post.ViewCount + 1, post.ContentHtml, series, post.PublishedAt, post.LikeCount, post.CollectCount, post.CommentCount));
    }
}
