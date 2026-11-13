using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelGuo.Data;
using ReelGuo.Data.Entities;

namespace ReelGuo.Api.Admin;

/// <summary>Playback quality for a slice of plays. Ratios are 0..1; null when there were no samples.</summary>
public sealed record QualityDto(int Samples, double? StartupP50Ms, double? StartupP95Ms, double? RebufferRatio, double? ErrorRate);

public sealed record AnalyticsDayDto(DateOnly Day, int Plays, int Sessions, int Completions, QualityDto Quality);

public sealed record AnalyticsBreakdownDto(string Key, QualityDto Quality);

public sealed record AnalyticsSeriesDto(long SeriesId, string Title, int Plays, int Sessions, int Completions, double CompletionRate);

public sealed record AnalyticsOverviewDto(
    int Days, DateTimeOffset From, DateTimeOffset To,
    int Plays, int Sessions, int Completions, double CompletionRate, double WatchHours, int NewMembers, int Comments,
    QualityDto Quality, List<AnalyticsDayDto> Daily,
    List<AnalyticsBreakdownDto> ByCountry, List<AnalyticsBreakdownDto> ByDevice, List<AnalyticsBreakdownDto> ByBrowser,
    List<AnalyticsSeriesDto> TopSeries);

/// <summary>数据概览: plays and playback quality from the anonymous play events and samples (kept 30 days, UTC days).</summary>
public static class AdminAnalyticsEndpoints
{
    public static RouteGroupBuilder MapAdminAnalytics(this RouteGroupBuilder admin)
    {
        admin.MapGet("/analytics/overview", Overview).WithTags("Admin: analytics")
            .WithSummary("Plays, completions and playback quality over the last N days (1-30, UTC).");
        return admin;
    }

    private sealed class PlayRow
    {
        public DateOnly Day { get; set; }
        public int Plays { get; set; }
        public int Sessions { get; set; }
        public int Completions { get; set; }
    }

    private sealed class QualityRow
    {
        public string Key { get; set; } = "";
        public int Samples { get; set; }
        public int Errors { get; set; }
        public double? StartupP50 { get; set; }
        public double? StartupP95 { get; set; }
        public long RebufferMs { get; set; }
        public double WatchedSeconds { get; set; }
    }

    private sealed class SeriesRow
    {
        public long SeriesId { get; set; }
        public int Plays { get; set; }
        public int Sessions { get; set; }
        public int Completions { get; set; }
    }

    private static QualityDto ToQuality(QualityRow? r) => r is null || r.Samples == 0
        ? new QualityDto(0, null, null, null, null)
        : new QualityDto(r.Samples, r.StartupP50, r.StartupP95,
            r.WatchedSeconds * 1000 + r.RebufferMs > 0 ? r.RebufferMs / (r.WatchedSeconds * 1000 + r.RebufferMs) : 0,
            (double)r.Errors / r.Samples);

    // Raw-SQL rows map by snake_case column names (the context's naming convention applies to them too).

    /// <summary>Quality grouped by one column of playback_samples ("" = everything).</summary>
    private static Task<List<QualityRow>> QualityAsync(ReelGuoDbContext db, string groupBy, DateTimeOffset from, CancellationToken ct)
    {
        // groupBy is one of the fixed expressions below, never user input.
        var key = groupBy switch
        {
            "day" => "to_char(at AT TIME ZONE 'UTC', 'YYYY-MM-DD')",
            "country" => "country",
            "device" => "device",
            "browser" => "browser",
            _ => "''",
        };
        var sql = $"""
            SELECT {key} AS key, count(*)::int AS samples, count(*) FILTER (WHERE error IS NOT NULL)::int AS errors,
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY startup_ms) AS startup_p50,
                   percentile_cont(0.95) WITHIN GROUP (ORDER BY startup_ms) AS startup_p95,
                   coalesce(sum(rebuffer_ms), 0)::bigint AS rebuffer_ms, coalesce(sum(watched_seconds), 0) AS watched_seconds
              FROM playback_samples WHERE at >= @from GROUP BY 1
            """;
        return db.Database.SqlQueryRaw<QualityRow>(sql, new Npgsql.NpgsqlParameter("from", from)).ToListAsync(ct);
    }

    private static async Task<Ok<AnalyticsOverviewDto>> Overview(ReelGuoDbContext db, TimeProvider time, [FromQuery] int? days, CancellationToken ct)
    {
        var d = Math.Clamp(days ?? 7, 1, 30);
        var now = time.GetUtcNow();
        var from = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddDays(1 - d);

        var plays = await db.Database.SqlQuery<PlayRow>($"""
            SELECT (at AT TIME ZONE 'UTC')::date AS day,
                   count(*) FILTER (WHERE kind = 'Start')::int AS plays,
                   count(DISTINCT session_id)::int AS sessions,
                   count(*) FILTER (WHERE kind = 'Complete')::int AS completions
              FROM play_events WHERE at >= {from} GROUP BY 1
            """).ToListAsync(ct);
        var totals = await db.Database.SqlQuery<PlayRow>($"""
            SELECT CURRENT_DATE AS day,
                   count(*) FILTER (WHERE kind = 'Start')::int AS plays,
                   count(DISTINCT session_id)::int AS sessions,
                   count(*) FILTER (WHERE kind = 'Complete')::int AS completions
              FROM play_events WHERE at >= {from}
            """).SingleAsync(ct);

        var dailyQuality = (await QualityAsync(db, "day", from, ct)).ToDictionary(r => r.Key);
        var overall = (await QualityAsync(db, "", from, ct)).SingleOrDefault();
        var byPlayDay = plays.ToDictionary(p => p.Day);
        var daily = Enumerable.Range(0, d).Select(i => DateOnly.FromDateTime(from.UtcDateTime.AddDays(i))).Select(day =>
        {
            var p = byPlayDay.GetValueOrDefault(day);
            return new AnalyticsDayDto(day, p?.Plays ?? 0, p?.Sessions ?? 0, p?.Completions ?? 0, ToQuality(dailyQuality.GetValueOrDefault(day.ToString("yyyy-MM-dd"))));
        }).ToList();

        List<AnalyticsBreakdownDto> Breakdown(List<QualityRow> rows, int take) =>
            rows.OrderByDescending(r => r.Samples).Take(take).Select(r => new AnalyticsBreakdownDto(r.Key.Trim(), ToQuality(r))).ToList();

        var top = await db.Database.SqlQuery<SeriesRow>($"""
            SELECT series_id AS series_id,
                   count(*) FILTER (WHERE kind = 'Start')::int AS plays,
                   count(DISTINCT session_id)::int AS sessions,
                   count(*) FILTER (WHERE kind = 'Complete')::int AS completions
              FROM play_events WHERE at >= {from} GROUP BY series_id ORDER BY 2 DESC LIMIT 10
            """).ToListAsync(ct);
        var ids = top.Select(t => t.SeriesId).ToList();
        var titles = await db.Series.Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Title, ct);

        var newMembers = await db.Users.CountAsync(u => u.Role == UserRole.User && u.CreatedAt >= from, ct);
        var comments = await db.Comments.CountAsync(c => c.CreatedAt >= from, ct);

        return TypedResults.Ok(new AnalyticsOverviewDto(
            d, from, now, totals.Plays, totals.Sessions, totals.Completions,
            totals.Plays > 0 ? Math.Min(1, (double)totals.Completions / totals.Plays) : 0,
            (overall?.WatchedSeconds ?? 0) / 3600, newMembers, comments, ToQuality(overall), daily,
            Breakdown(await QualityAsync(db, "country", from, ct), 10),
            Breakdown(await QualityAsync(db, "device", from, ct), 5),
            Breakdown(await QualityAsync(db, "browser", from, ct), 6),
            top.Select(t => new AnalyticsSeriesDto(t.SeriesId, titles.GetValueOrDefault(t.SeriesId, "(已删除)"), t.Plays, t.Sessions, t.Completions,
                t.Plays > 0 ? Math.Min(1, (double)t.Completions / t.Plays) : 0)).ToList()));
    }
}
