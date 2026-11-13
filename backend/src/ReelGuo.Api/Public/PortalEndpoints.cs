using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelGuo.Api.Geo;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Api.Users;
using ReelGuo.Data;
using ReelGuo.Data.Entities;
using ReelGuo.Data.Media;

namespace ReelGuo.Api.Public;

public sealed class SiteOptions
{
    public const string Section = "Site";

    /// <summary>Line under the site name (hero, footer).</summary>
    public string Tagline { get; set; } = "AI短剧与原创短剧在线观看";
    /// <summary>Official community link (e.g. a Telegram channel), shown with the official addresses.</summary>
    public string Telegram { get; set; } = "";
    /// <summary>Contact address that auto-replies with the latest addresses.</summary>
    public string Email { get; set; } = "";
    /// <summary>The domain that always points to the current address (e.g. a pages.dev or long-lived domain).</summary>
    public string PermanentDomain { get; set; } = "";
}

public sealed record AnnouncementDto(string Text, string? Url);

/// <summary>
/// Site chrome: name, tagline, the 公告 bar, and the official addresses (mirrors, permanent domain, contacts) shown
/// on 回家的路, in the footer and in the address reminder.
/// </summary>
public sealed record SiteDto(
    string Name, string Tagline, List<AnnouncementDto> Announcements, List<string> Mirrors, string? PermanentDomain, string? Telegram, string? Email);

public sealed record TopicCardDto(int Id, string Title, string Description, int Count, string? Cover);

public sealed record TopicPage(List<TopicCardDto> Items, PaginationDto Pagination);

public sealed record TopicDetailDto(int Id, string Title, string Description, List<VideoCardDto> Items);

public sealed record RateRequest(int Score);

/// <summary>The viewer's own rating (1-10) and the series totals.</summary>
public sealed record MyRatingDto(int? Mine, RatingDto Rating);

public static class PortalEndpoints
{
    public static RouteGroupBuilder MapPortal(this RouteGroupBuilder v1)
    {
        v1.MapGet("/site", Site).WithTags("App").WithSummary("Site name, announcements and official addresses.");

        var topics = v1.MapGroup("/topics").WithTags("Catalog");
        topics.MapGet("/", ListTopics).WithSummary("专题: published collections with their visible series count and cover.");
        topics.MapGet("/{id:int}", GetTopic).WithSummary("One collection with the series this visitor may watch.");

        var rating = v1.MapGroup("/series/{id:long}/rating").WithTags("Social").RequireAuthorization(UserAuth.Policy);
        rating.MapGet("/", MyRating).WithSummary("The viewer's rating of a series.");
        rating.MapPut("/", Rate).WithSummary("Rates a series 1-10 (replaces an earlier rating).");
        return v1;
    }

    internal static async Task<RatingDto> RatingAsync(ReelGuoDbContext db, long seriesId, CancellationToken ct)
    {
        var stats = await db.SeriesRatings.Where(r => r.SeriesId == seriesId)
            .GroupBy(_ => 1).Select(g => new { Count = g.Count(), Average = g.Average(r => (double)r.Score) })
            .FirstOrDefaultAsync(ct);
        return stats is null ? new RatingDto(null, 0) : new RatingDto(Math.Round(stats.Average, 1), stats.Count);
    }

    private static async Task<Ok<SiteDto>> Site(ReelGuoDbContext db, IOptions<SiteOptions> site, IOptions<AppOptions> app, CancellationToken ct)
    {
        var s = site.Value;
        var notices = await db.Announcements.AsNoTracking().Where(a => a.Active).OrderBy(a => a.SortOrder).ThenByDescending(a => a.Id)
            .Select(a => new AnnouncementDto(a.Text, a.Url)).ToListAsync(ct);
        var mirrors = app.Value.Mirrors.Select(m => m.Trim().TrimEnd('/')).Where(m => m.Length > 0).Distinct().ToList();
        static string? Clean(string v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        return TypedResults.Ok(new SiteDto("ReelGuo", s.Tagline, notices, mirrors, Clean(s.PermanentDomain), Clean(s.Telegram), Clean(s.Email)));
    }

    private static async Task<Ok<TopicPage>> ListTopics(
        HttpContext http, ReelGuoDbContext db, ICountryResolver resolver, MediaUrlSigner signer, TimeProvider time,
        [FromQuery] int? page, [FromQuery] int? size, CancellationToken ct)
    {
        var p = Math.Max(1, page ?? 1);
        var s = Math.Clamp(size ?? 24, 1, 48);
        var visible = db.Series.AsNoTracking().VisibleTo(resolver.Resolve(http), PublicEndpoints.Today(time));
        var query = db.Topics.AsNoTracking().Where(t => t.Published);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(t => t.SortOrder).ThenByDescending(t => t.Id).Skip((p - 1) * s).Take(s)
            .Select(t => new
            {
                t.Id, t.Title, t.Description,
                Count = t.Items.Count(i => visible.Any(v => v.Id == i.SeriesId)),
                Cover = t.Items.Where(i => visible.Any(v => v.Id == i.SeriesId)).OrderBy(i => i.Position).Select(i => i.Series.CoverKey).FirstOrDefault(),
            })
            .ToListAsync(ct);
        var items = rows.Select(r => new TopicCardDto(r.Id, r.Title, r.Description, r.Count, signer.PublicUrlOrNull(r.Cover))).ToList();
        return TypedResults.Ok(new TopicPage(items, new PaginationDto(p, Math.Max(1, (int)Math.Ceiling(total / (double)s)), s, total)));
    }

    private static async Task<Results<Ok<TopicDetailDto>, NotFound<ApiErrorResponse>>> GetTopic(
        int id, HttpContext http, ReelGuoDbContext db, ICountryResolver resolver, MediaUrlSigner signer, TimeProvider time, CancellationToken ct)
    {
        var topic = await db.Topics.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && t.Published, ct);
        if (topic is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "专题不存在"));
        var order = await db.TopicSeries.AsNoTracking().Where(i => i.TopicId == id).OrderBy(i => i.Position).Select(i => i.SeriesId).ToListAsync(ct);
        var series = await db.Series.AsNoTracking().VisibleTo(resolver.Resolve(http), PublicEndpoints.Today(time))
            .Where(s => order.Contains(s.Id)).Include(s => s.Author).Include(s => s.Category).ToListAsync(ct);
        var items = series.OrderBy(s => order.IndexOf(s.Id)).Select(s => PublicEndpoints.ToCard(s, signer)).ToList();
        return TypedResults.Ok(new TopicDetailDto(topic.Id, topic.Title, topic.Description, items));
    }

    private static async Task<Ok<MyRatingDto>> MyRating(long id, HttpContext http, ReelGuoDbContext db, CancellationToken ct)
    {
        var uid = UserAuth.UserId(http);
        var mine = await db.SeriesRatings.Where(r => r.UserId == uid && r.SeriesId == id).Select(r => (int?)r.Score).FirstOrDefaultAsync(ct);
        return TypedResults.Ok(new MyRatingDto(mine, await RatingAsync(db, id, ct)));
    }

    private static async Task<Results<Ok<MyRatingDto>, NotFound<ApiErrorResponse>, BadRequest<ApiErrorResponse>>> Rate(
        long id, RateRequest request, HttpContext http, ReelGuoDbContext db, ICountryResolver resolver, TimeProvider time, CancellationToken ct)
    {
        if (request.Score is < 1 or > 10) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_score", "评分需为 1-10 分"));
        if (!await db.Series.VisibleTo(resolver.Resolve(http), PublicEndpoints.Today(time)).AnyAsync(s => s.Id == id, ct))
            return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "内容不存在或已下架"));
        var uid = UserAuth.UserId(http);
        var now = time.GetUtcNow();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO series_ratings (user_id, series_id, score, created_at, updated_at) VALUES ({uid}, {id}, {request.Score}, {now}, {now})
            ON CONFLICT (user_id, series_id) DO UPDATE SET score = excluded.score, updated_at = excluded.updated_at
            """, ct);
        return TypedResults.Ok(new MyRatingDto(request.Score, await RatingAsync(db, id, ct)));
    }
}
