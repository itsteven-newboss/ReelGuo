using Microsoft.EntityFrameworkCore;
using Npgsql;
using ReelGuo.Data.Entities;

namespace ReelGuo.Data.Catalog;

/// <summary>Per-series inputs to the rankings: interaction counters plus play signals from play_events.</summary>
public sealed record SeriesSignals(
    long SeriesId,
    DateTimeOffset? PublishedAt,
    int RecommendWeight,
    long Likes,
    long Collects,
    long Comments,
    /// <summary>Distinct sessions that started the series in the last 7 days.</summary>
    long Plays7,
    /// <summary>Distinct (session, episode) completions in the last 7 days.</summary>
    long Completes7,
    long Plays3,
    long PlaysPrev3,
    /// <summary>Distinct sessions over the retained window (30 days); shown as the play count.</summary>
    long PlaysRetained);

/// <summary>Pure scoring rules (UI-REFERENCE §9: 热播 / 推荐 / 潜力).</summary>
public static class RankingMath
{
    public const int PotentialWindowDays = 14;

    /// <summary>Plays and interactions over 7 days, gently decayed by age so new hits can overtake old ones.</summary>
    public static double Hot(SeriesSignals s, DateTimeOffset now)
    {
        var raw = s.Plays7 + 3.0 * s.Likes + 5.0 * s.Collects + 4.0 * s.Comments + 10.0 * s.Completes7;
        var ageDays = s.PublishedAt is { } p ? Math.Max(0, (now - p).TotalDays) : 0;
        return Math.Round(raw / Math.Sqrt(1 + ageDays / 14.0), 2);
    }

    public static double CompletionRate(SeriesSignals s) =>
        s.Plays7 == 0 ? 0 : Math.Min(1, s.Completes7 / (double)s.Plays7);

    /// <summary>
    /// Editor picks always rank above non-picks (weight × 1000 outweighs the 0-100 completion score),
    /// then completion rate. Null = not listed.
    /// </summary>
    public static double? Recommend(SeriesSignals s) =>
        s.RecommendWeight <= 0 && s.Plays7 == 0 ? null : Math.Round(s.RecommendWeight * 1000 + CompletionRate(s) * 100, 1);

    /// <summary>Recently published series, by recent plays and growth; fresher breaks ties. Null = too old.</summary>
    public static double? Potential(SeriesSignals s, DateTimeOffset now)
    {
        if (s.PublishedAt is not { } p) return null;
        var ageDays = (now - p).TotalDays;
        if (ageDays > PotentialWindowDays) return null;
        var growth = Math.Max(0, s.Plays3 - s.PlaysPrev3);
        return Math.Round(s.Plays3 + 2.0 * growth + (PotentialWindowDays - ageDays) * 0.1, 1);
    }
}

public sealed record RankingRun(int Series, int Hot, int Recommend, int Potential, int EventsDeleted);

/// <summary>Recomputes hot scores, play counts and the three rankings. Run by the worker every 10 minutes.</summary>
public sealed class RankingCalculator(ReelGuoDbContext db, TimeProvider time)
{
    public const int TopN = 50;
    public const int RetentionDays = 30;
    /// <summary>Pending orders older than this are marked expired.</summary>
    public const int OrderExpiryHours = 24;
    /// <summary>pg advisory lock so only one worker recomputes at a time.</summary>
    public const long LockKey = 7_302_001;

    public async Task<RankingRun?> RecomputeAsync(CancellationToken cancellationToken)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            if (!await ScalarAsync<bool>(conn, "SELECT pg_try_advisory_lock(@k)", cancellationToken, ("k", LockKey)))
                return null; // another worker is on it
            try
            {
                return await RunAsync(conn, cancellationToken);
            }
            finally
            {
                await ScalarAsync<bool>(conn, "SELECT pg_advisory_unlock(@k)", CancellationToken.None, ("k", LockKey));
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private async Task<RankingRun> RunAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var series = await db.Series.AsNoTracking()
            .Where(s => s.Status == SeriesStatus.Published)
            .Select(s => new { s.Id, s.PublishedAt, s.RecommendWeight, s.LikeCount, s.CollectCount, s.CommentCount })
            .ToListAsync(ct);

        var plays = new Dictionary<long, (long P7, long C7, long P3, long Pp3, long PAll)>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT series_id,
                   COUNT(DISTINCT session_id) FILTER (WHERE kind = 'Start' AND at > @d7),
                   COUNT(DISTINCT (session_id, episode_number)) FILTER (WHERE kind = 'Complete' AND at > @d7),
                   COUNT(DISTINCT session_id) FILTER (WHERE kind = 'Start' AND at > @d3),
                   COUNT(DISTINCT session_id) FILTER (WHERE kind = 'Start' AND at > @d6 AND at <= @d3),
                   COUNT(DISTINCT session_id) FILTER (WHERE kind = 'Start')
              FROM play_events
             GROUP BY series_id
            """, conn))
        {
            cmd.Parameters.AddWithValue("d7", now.AddDays(-7));
            cmd.Parameters.AddWithValue("d6", now.AddDays(-6));
            cmd.Parameters.AddWithValue("d3", now.AddDays(-3));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                plays[reader.GetInt64(0)] = (reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5));
        }

        var signals = series.Select(s =>
        {
            var p = plays.GetValueOrDefault(s.Id);
            return new SeriesSignals(s.Id, s.PublishedAt, s.RecommendWeight, s.LikeCount, s.CollectCount, s.CommentCount, p.P7, p.C7, p.P3, p.Pp3, p.PAll);
        }).ToList();

        var hot = signals.Select(s => (s.SeriesId, Score: RankingMath.Hot(s, now))).ToList();
        var recommend = signals.Select(s => (s.SeriesId, Score: RankingMath.Recommend(s))).Where(x => x.Score is not null)
            .Select(x => (x.SeriesId, Score: x.Score!.Value)).ToList();
        var potential = signals.Select(s => (s.SeriesId, Score: RankingMath.Potential(s, now))).Where(x => x.Score is not null)
            .Select(x => (x.SeriesId, Score: x.Score!.Value)).ToList();

        await using var tx = await conn.BeginTransactionAsync(ct);

        // Scores and play counts in one statement.
        await using (var update = new NpgsqlCommand("""
            UPDATE series s SET hot_score = v.hot, play_count = v.plays
              FROM unnest(@ids, @hots, @plays) AS v(id, hot, plays)
             WHERE s.id = v.id
            """, conn, tx))
        {
            update.Parameters.AddWithValue("ids", signals.Select(s => s.SeriesId).ToArray());
            update.Parameters.AddWithValue("hots", hot.Select(h => h.Score).ToArray());
            update.Parameters.AddWithValue("plays", signals.Select(s => s.PlaysRetained).ToArray());
            await update.ExecuteNonQueryAsync(ct);
        }

        await using (var clear = new NpgsqlCommand("DELETE FROM ranks", conn, tx))
            await clear.ExecuteNonQueryAsync(ct);

        async Task WriteAsync(RankKind kind, List<(long SeriesId, double Score)> rows)
        {
            var top = rows.OrderByDescending(r => r.Score).ThenByDescending(r => r.SeriesId).Take(TopN).ToList();
            if (top.Count == 0) return;
            await using var insert = new NpgsqlCommand("""
                INSERT INTO ranks (kind, position, series_id, metric_value, computed_at)
                SELECT @kind, v.pos, v.id, v.metric, @at FROM unnest(@pos, @ids, @metrics) AS v(pos, id, metric)
                """, conn, tx);
            insert.Parameters.AddWithValue("kind", kind.ToString());
            insert.Parameters.AddWithValue("at", now);
            insert.Parameters.AddWithValue("pos", Enumerable.Range(1, top.Count).ToArray());
            insert.Parameters.AddWithValue("ids", top.Select(t => t.SeriesId).ToArray());
            insert.Parameters.AddWithValue("metrics", top.Select(t => t.Score).ToArray());
            await insert.ExecuteNonQueryAsync(ct);
        }

        await WriteAsync(RankKind.Hot, hot.Where(h => h.Score > 0).ToList());
        await WriteAsync(RankKind.Recommend, recommend);
        await WriteAsync(RankKind.Potential, potential);

        int deleted;
        await using (var prune = new NpgsqlCommand("DELETE FROM play_events WHERE at < @cut", conn, tx))
        {
            prune.Parameters.AddWithValue("cut", now.AddDays(-RetentionDays));
            deleted = await prune.ExecuteNonQueryAsync(ct);
        }
        await using (var pruneSamples = new NpgsqlCommand("DELETE FROM playback_samples WHERE at < @cut", conn, tx))
        {
            pruneSamples.Parameters.AddWithValue("cut", now.AddDays(-RetentionDays));
            await pruneSamples.ExecuteNonQueryAsync(ct);
        }
        // Checkouts abandoned for a day. A late payment notice for one is still honoured (PaymentProcessor).
        await using (var expire = new NpgsqlCommand(
            "UPDATE orders SET status = 'Expired', updated_at = @now WHERE status = 'Pending' AND created_at < @cut", conn, tx))
        {
            expire.Parameters.AddWithValue("now", now);
            expire.Parameters.AddWithValue("cut", now.AddHours(-OrderExpiryHours));
            await expire.ExecuteNonQueryAsync(ct);
        }
        // Notices that failed verification are kept a month for investigation; genuine ones are kept for good.
        await using (var pruneRejected = new NpgsqlCommand(
            "DELETE FROM payment_events WHERE outcome = 'Rejected' AND received_at < @cut", conn, tx))
        {
            pruneRejected.Parameters.AddWithValue("cut", now.AddDays(-RetentionDays));
            await pruneRejected.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return new RankingRun(signals.Count, Math.Min(TopN, hot.Count(h => h.Score > 0)), Math.Min(TopN, recommend.Count), Math.Min(TopN, potential.Count), deleted);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection conn, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return (T)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
