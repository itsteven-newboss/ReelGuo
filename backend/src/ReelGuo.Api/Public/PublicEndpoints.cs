using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelGuo.Api.Billing;
using ReelGuo.Api.Geo;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Api.Social;
using ReelGuo.Api.Users;
using ReelGuo.Data;
using ReelGuo.Data.Catalog;
using ReelGuo.Data.Entities;
using ReelGuo.Data.Media;

namespace ReelGuo.Api.Public;

/// <summary>
/// Boot configuration. <c>age_check</c> = strict means the visitor's country requires a verified-age account.
/// <c>monetization</c>: paid episodes, coins and VIP are switched on.
/// </summary>
public sealed record AppConfigDto(string AppName, string DefaultLocale, string ApiVersion, DateTimeOffset ServerTime, string Country, AgeCheckMode AgeCheck, bool Monetization);

/// <summary>A browse channel. <c>coming_soon</c> channels are listed but have no content or actions.</summary>
public sealed record CategoryDto(string Slug, string Name, string? Description, CategoryStatus Status);

public sealed record AuthorDto(long Id, string Name, string? Avatar);

/// <summary>Card shape used by the feed and every list (matches the reference client's VideoCard), plus its channel.</summary>
public sealed record VideoCardDto(
    long Id, string Title, string? Cover, string Description, List<string> Tags, double Score,
    int EpisodeCount, int TotalEpisodes, long Hot, long LikeCount, long CollectCount, long CommentCount,
    bool IsFinished, bool IsOriginal, AuthorDto? Author, DateTimeOffset? PublishedAt, DateTimeOffset? LastEpisodeAt,
    string? CategorySlug = null, string? CategoryName = null);

public sealed record PaginationDto(int Page, int Pages, int Size, int Total);

public sealed record VideoCardPage(List<VideoCardDto> Items, PaginationDto Pagination);

/// <summary><c>is_free</c>: anyone can watch it. Otherwise <c>price</c> coins unlock it (or VIP covers it).</summary>
public sealed record EpisodeDto(int EpNum, string Title, double Duration, bool IsFree, int Price);

public sealed record CountsDto(long Collect, long Comment, long Hot, long Like);

/// <summary>Viewer ratings (点我评分): average of 1-10 scores, null until someone rates.</summary>
public sealed record RatingDto(double? Average, int Count);

public sealed record SeriesDetailDto(
    long Id, string Title, string Description, string? Cover, List<string> Tags, double Score,
    int EpisodeCount, int TotalEpisodes, List<EpisodeDto> Episodes, CountsDto Counts, AuthorDto? Author,
    bool IsFinished, bool IsOriginal, string CategorySlug, string CategoryName, DateTimeOffset? PublishedAt,
    DateTimeOffset? LastEpisodeAt, RatingDto Rating);

public sealed record PlayInfoDto(
    long Id, int EpNum, string Title, double Duration, string VideoUrl, DateTimeOffset ExpiresAt,
    string? Poster, int Width, int Height);

public static class PublicEndpoints
{
    public static RouteGroupBuilder MapPublicApi(this IEndpointRouteBuilder app)
    {
        // Every /api/v1 route can also return 451 { error: { code: "region_blocked" } } from GeoBlockingMiddleware.
        var v1 = app.MapGroup("/api/v1")
            .WithMetadata(new ProducesResponseTypeMetadata(StatusCodes.Status451UnavailableForLegalReasons, typeof(ApiErrorResponse), ["application/json"]));

        v1.MapGet("/config", async (HttpContext http, ICountryResolver resolver, IAgeRuleStore ageRules, IOptions<BillingOptions> billing, TimeProvider time, CancellationToken ct) =>
            {
                var country = resolver.Resolve(http);
                return TypedResults.Ok(new AppConfigDto("ReelGuo", "zh-CN", "1", time.GetUtcNow(), country,
                    await ageRules.ModeForAsync(country, ct), billing.Value.Enabled));
            })
            .WithTags("App")
            .WithSummary("Boot configuration. The app calls this first; a 451 here means the visitor's region is blocked.");

        v1.MapGet("/categories", ListCategories).WithTags("Catalog")
            .WithSummary("Channels shown on the Category tab, in display order.");
        v1.MapGet("/categories/{slug}/series", ListCategorySeries).WithTags("Catalog")
            .WithSummary("Published series in a channel. sort = latest (最近更新) | hot (当前热播) | picks (精选推荐) | original (原创) | random | score.");
        v1.MapGet("/feed", Feed).WithTags("Catalog")
            .WithSummary("Series across channels. sort = latest (newest published) | updated (最近上新) | hot | picks (精选推荐) | random.");
        v1.MapGet("/series/{id:long}", GetSeries).WithTags("Catalog")
            .WithSummary("Series detail with its episode list. 404 when not published, not licensed here, or unknown.");
        v1.MapGet("/series/{id:long}/episodes/{ep:int}/play", Play).WithTags("Playback")
            .Produces<ApiErrorResponse>(StatusCodes.Status402PaymentRequired)
            .WithSummary("Signed, time-limited HLS URL for one episode. 402 episode_locked when it must be unlocked first.");

        v1.MapDiscovery();
        v1.MapAccounts();
        v1.MapSocial();
        v1.MapBilling();
        v1.MapAppReleases();
        v1.MapPortal();

        return v1;
    }

    /// <summary>Series a visitor from <paramref name="country"/> may see today.</summary>
    internal static IQueryable<Series> VisibleTo(this IQueryable<Series> query, string country, DateOnly today) =>
        query.Where(s => s.Status == SeriesStatus.Published
            && s.Category.Status == CategoryStatus.Active
            && s.EpisodeCount > 0
            && (s.Territories.Contains(PublishRules.Worldwide) || s.Territories.Contains(country))
            && s.LicenceStart <= today
            && (s.LicenceEnd == null || s.LicenceEnd >= today));

    internal static DateOnly Today(TimeProvider time) => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    private static async Task<Ok<List<CategoryDto>>> ListCategories(ReelGuoDbContext db, CancellationToken ct)
    {
        var rows = await db.Categories.AsNoTracking()
            .Where(c => c.Status != CategoryStatus.Hidden)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
            .Select(c => new CategoryDto(c.Slug, c.Name, c.Description, c.Status))
            .ToListAsync(ct);
        return TypedResults.Ok(rows);
    }

    private static async Task<Ok<VideoCardPage>> Feed(
        HttpContext http, ReelGuoDbContext db, ICountryResolver resolver, MediaUrlSigner signer, TimeProvider time,
        [FromQuery] string? sort, [FromQuery] int? page, [FromQuery] int? size, CancellationToken ct)
    {
        var visible = db.Series.AsNoTracking().VisibleTo(resolver.Resolve(http), Today(time));
        if (sort == "picks")
        {
            // Editor picks first (推荐权重), topped up with the hottest series when there are few picks.
            var picks = visible.Where(s => s.RecommendWeight > 0).OrderByDescending(s => s.RecommendWeight).ThenByDescending(s => s.HotScore);
            if (await picks.CountAsync(ct) >= Math.Clamp(size ?? 10, 1, 30)) return TypedResults.Ok(await PageAsync(picks, signer, page, size, ct));
            return TypedResults.Ok(await PageAsync(
                visible.OrderByDescending(s => s.RecommendWeight).ThenByDescending(s => s.HotScore).ThenByDescending(s => s.Id), signer, page, size, ct));
        }
        return TypedResults.Ok(await PageAsync(Sorted(visible, sort ?? "latest"), signer, page, size, ct));
    }

    /// <summary>Shared orderings. "updated" puts the most recently updated series first (最近上新 / 最新更新).</summary>
    internal static IQueryable<Series> Sorted(IQueryable<Series> visible, string sort) => sort switch
    {
        "updated" => visible.OrderByDescending(s => s.LastEpisodeAt ?? s.PublishedAt).ThenByDescending(s => s.Id),
        "hot" => visible.OrderByDescending(s => s.HotScore).ThenByDescending(s => s.PlayCount).ThenByDescending(s => s.Id),
        "score" => visible.OrderByDescending(s => s.Score).ThenByDescending(s => s.Id),
        "picks" => visible.OrderByDescending(s => s.RecommendWeight).ThenByDescending(s => s.HotScore).ThenByDescending(s => s.Id),
        "original" => visible.Where(s => s.IsOriginal).OrderByDescending(s => s.LastEpisodeAt ?? s.PublishedAt).ThenByDescending(s => s.Id),
        "random" => visible.OrderBy(_ => EF.Functions.Random()),
        _ => visible.OrderByDescending(s => s.PublishedAt).ThenByDescending(s => s.Id),
    };

    private static async Task<Results<Ok<VideoCardPage>, NotFound<ApiErrorResponse>>> ListCategorySeries(
        string slug, HttpContext http, ReelGuoDbContext db, ICountryResolver resolver, MediaUrlSigner signer, TimeProvider time,
        [FromQuery] string? sort, [FromQuery] int? page, [FromQuery] int? size, CancellationToken ct)
    {
        var category = await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Slug == slug && c.Status != CategoryStatus.Hidden, ct);
        if (category is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "频道不存在"));

        var visible = db.Series.AsNoTracking().VisibleTo(resolver.Resolve(http), Today(time)).Where(s => s.CategoryId == category.Id);
        // A channel's "latest" means latest updated, as on its 最新更新 tab.
        return TypedResults.Ok(await PageAsync(Sorted(visible, sort is null or "latest" ? "updated" : sort), signer, page, size, ct));
    }

    internal static async Task<VideoCardPage> PageAsync(IQueryable<Series> query, MediaUrlSigner signer, int? page, int? size, CancellationToken ct)
    {
        var p = Math.Max(1, page ?? 1);
        var s = Math.Clamp(size ?? 10, 1, 30);
        var total = await query.CountAsync(ct);
        var rows = await query.Skip((p - 1) * s).Take(s).Include(x => x.Author).Include(x => x.Category).ToListAsync(ct);
        var items = rows.Select(x => ToCard(x, signer)).ToList();
        return new VideoCardPage(items, new PaginationDto(p, Math.Max(1, (int)Math.Ceiling(total / (double)s)), s, total));
    }

    private static AuthorDto? ToAuthor(User? u) => u is null ? null : new AuthorDto(u.Id, u.Nickname, u.AvatarUrl);

    internal static VideoCardDto ToCard(Series s, MediaUrlSigner signer) => new(
        s.Id, s.Title, signer.PublicUrlOrNull(s.CoverKey), s.Description, s.Tags, s.Score,
        s.EpisodeCount, Math.Max(s.TotalEpisodes, s.EpisodeCount), s.PlayCount, s.LikeCount, s.CollectCount, s.CommentCount,
        s.IsFinished, s.IsOriginal, ToAuthor(s.Author), s.PublishedAt, s.LastEpisodeAt, s.Category?.Slug, s.Category?.Name);

    private static async Task<Results<Ok<SeriesDetailDto>, NotFound<ApiErrorResponse>>> GetSeries(
        long id, HttpContext http, ReelGuoDbContext db, ICountryResolver resolver, MediaUrlSigner signer, IOptions<BillingOptions> billing,
        TimeProvider time, CancellationToken ct)
    {
        var s = await db.Series.AsNoTracking().VisibleTo(resolver.Resolve(http), Today(time))
            .Include(x => x.Author).Include(x => x.Category)
            .Include(x => x.Episodes.Where(e => e.Status == EpisodeStatus.Ready).OrderBy(e => e.EpisodeNumber))
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "内容不存在或已下架"));

        return TypedResults.Ok(new SeriesDetailDto(
            s.Id, s.Title, s.Description, signer.PublicUrlOrNull(s.CoverKey), s.Tags, s.Score,
            s.EpisodeCount, Math.Max(s.TotalEpisodes, s.EpisodeCount),
            s.Episodes.Select(e =>
            {
                var price = Pricing.EpisodePrice(billing.Value.Enabled, s.EpisodePrice, s.FreeEpisodes, e.EpisodeNumber, e.IsFree);
                return new EpisodeDto(e.EpisodeNumber, e.Title, e.DurationSeconds, price == 0, price);
            }).ToList(),
            new CountsDto(s.CollectCount, s.CommentCount, s.PlayCount, s.LikeCount), ToAuthor(s.Author),
            s.IsFinished, s.IsOriginal, s.Category.Slug, s.Category.Name, s.PublishedAt, s.LastEpisodeAt,
            await PortalEndpoints.RatingAsync(db, s.Id, ct)));
    }

    private static async Task<Results<Ok<PlayInfoDto>, NotFound<ApiErrorResponse>, JsonHttpResult<ApiErrorResponse>>> Play(
        long id, int ep, HttpContext http, ReelGuoDbContext db, ICountryResolver resolver, MediaUrlSigner signer, WalletService wallets,
        IOptions<BillingOptions> billing, TimeProvider time, CancellationToken ct)
    {
        var row = await db.Episodes.AsNoTracking()
            .Where(e => e.EpisodeNumber == ep && e.Status == EpisodeStatus.Ready && e.HlsPrefix != null)
            .Where(e => db.Series.VisibleTo(resolver.Resolve(http), Today(time)).Any(s => s.Id == id && s.Id == e.SeriesId))
            .Select(e => new { Episode = e, e.Series.EpisodePrice, e.Series.FreeEpisodes })
            .FirstOrDefaultAsync(ct);
        if (row is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "该集暂时无法播放"));
        var episode = row.Episode;

        // Paid episodes: only for viewers who bought them or have VIP. This is the only place that hands out media
        // links, and the links are per episode, so this check covers the media too.
        if (Pricing.EpisodePrice(billing.Value.Enabled, row.EpisodePrice, row.FreeEpisodes, ep, episode.IsFree) > 0
            && (await UserAuth.OptionalUserIdAsync(http) is not { } uid || !await wallets.CanWatchAsync(uid, id, ep, ct)))
        {
            http.Response.Headers.CacheControl = "no-store";
            return TypedResults.Json(ApiErrorResponse.Of("episode_locked", "本集需解锁后观看"), statusCode: StatusCodes.Status402PaymentRequired);
        }

        var (url, expiresAt) = signer.PlaybackUrl(episode.HlsPrefix!);
        return TypedResults.Ok(new PlayInfoDto(
            id, episode.EpisodeNumber, episode.Title, episode.DurationSeconds, url, expiresAt,
            signer.PublicUrlOrNull(episode.PosterKey), episode.Width, episode.Height));
    }
}
