using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Api.Media;
using ReelGuo.Data;
using ReelGuo.Data.Entities;
using ReelGuo.Data.Media;
using ReelGuo.Data.Storage;

namespace ReelGuo.Api.Admin;

public sealed record DeleteSeriesRequest(List<long> Ids);

/// <summary>What deleting one series takes with it, shown before the admin confirms.</summary>
public sealed record SeriesDeleteImpactDto(
    long Id, string Title, SeriesStatus Status, int Episodes, int Buyers, int Comments, int CosFiles);

/// <param name="CosDeleted">Video files removed from COS.</param>
/// <param name="CosFailed">Video files COS did not delete (see 运行追踪 → 日志 for the keys).</param>
/// <param name="CosKept">Video files still used by another episode, so left in place.</param>
public sealed record DeleteSeriesResult(int Deleted, List<long> Ids, int CosDeleted, int CosFailed, int CosKept);

/// <summary>
/// Permanent deletion of series, one or many at a time. The database takes episodes, processing jobs, likes, favourites,
/// watch history, ratings, rankings, collection entries and episode purchases with the series (foreign keys); articles
/// that mention it keep existing without the link. Comments on it are deleted here. Wallet ledger entries stay, since
/// they are the record of what viewers paid. After the commit the files go too: local media (HLS, posters, cover,
/// uploaded originals) and the episodes' COS videos, unless another episode still uses the same COS file.
/// </summary>
public static class AdminSeriesDeletion
{
    public const int MaxBatch = 100;

    public static RouteGroupBuilder MapAdminSeriesDeletion(this RouteGroupBuilder series)
    {
        series.MapPost("/delete-preview", Preview)
            .WithSummary("What deleting these series would remove: status, episodes, buyers, comments, COS videos.");
        series.MapPost("/delete", Delete)
            .WithSummary("Permanently deletes series with their episodes, media (local and COS), episode purchases and comments.");
        return series;
    }

    private static List<long>? IdsOf(DeleteSeriesRequest request)
    {
        var ids = (request.Ids ?? []).Distinct().ToList();
        return ids.Count is 0 or > MaxBatch ? null : ids;
    }

    private static Task<List<SeriesDeleteImpactDto>> ImpactAsync(ReelGuoDbContext db, List<long> ids, CancellationToken ct) =>
        db.Series.AsNoTracking().Where(s => ids.Contains(s.Id)).OrderBy(s => s.Id)
            .Select(s => new SeriesDeleteImpactDto(
                s.Id, s.Title, s.Status,
                s.Episodes.Count(),
                db.EpisodeUnlocks.Where(u => u.SeriesId == s.Id).Select(u => u.UserId).Distinct().Count(),
                db.Comments.Count(c => c.TargetType == CommentTarget.Series && c.TargetId == s.Id),
                s.Episodes.Count(e => (e.SourceKey != null && e.SourceKey.StartsWith("videos/")) || (e.VideoKey != null && e.VideoKey.StartsWith("videos/")))))
            .ToListAsync(ct);

    private static async Task<Results<Ok<List<SeriesDeleteImpactDto>>, BadRequest<ApiErrorResponse>>> Preview(
        DeleteSeriesRequest request, ReelGuoDbContext db, CancellationToken ct)
    {
        var ids = IdsOf(request);
        if (ids is null) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_ids", $"一次可删除 1-{MaxBatch} 部剧"));
        return TypedResults.Ok(await ImpactAsync(db, ids, ct));
    }

    private static async Task<Results<Ok<DeleteSeriesResult>, BadRequest<ApiErrorResponse>>> Delete(
        DeleteSeriesRequest request, HttpContext http, ReelGuoDbContext db, StorageBuckets storage, CosVideoStore cos,
        IRevokedSeriesStore revoked, TimeProvider time, ILoggerFactory logs, CancellationToken ct)
    {
        var ids = IdsOf(request);
        if (ids is null) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_ids", $"一次可删除 1-{MaxBatch} 部剧"));

        var impact = await ImpactAsync(db, ids, ct);
        var found = impact.Select(i => i.Id).ToList();
        if (found.Count == 0) return TypedResults.Ok(new DeleteSeriesResult(0, [], 0, 0, 0));

        // What to clean up in storage once the rows are gone.
        var episodes = await db.Episodes.AsNoTracking().Where(e => found.Contains(e.SeriesId))
            .Select(e => new { e.SeriesId, e.SourceKey, e.VideoKey, e.PosterKey }).ToListAsync(ct);
        var covers = await db.Series.AsNoTracking().Where(s => found.Contains(s.Id) && s.CoverKey != null)
            .Select(s => new { s.Id, Key = s.CoverKey! }).ToListAsync(ct);

        var now = time.GetUtcNow();
        var actor = AdminActor.From(http);
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // Comments point at their series by id only, so they are removed here (replies and likes follow by key).
            await db.Comments.Where(c => c.TargetType == CommentTarget.Series && found.Contains(c.TargetId)).ExecuteDeleteAsync(ct);
            await db.Series.Where(s => found.Contains(s.Id)).ExecuteDeleteAsync(ct);
            foreach (var i in impact)
                db.AddAudit(now, actor, "series.delete", "series", i.Id.ToString(),
                    new { i.Title, status = i.Status.ToString(), episodes = i.Episodes, buyers = i.Buyers, comments = i.Comments, cos_files = i.CosFiles });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        revoked.Invalidate();

        // Storage, best effort: the series is already gone, so a leftover file is only wasted space. Runs to the end even
        // if the admin's request is cancelled now.
        var log = logs.CreateLogger("AdminCatalog");
        async Task TryAsync(Func<Task> delete, string what)
        {
            try { await delete(); }
            catch (Exception ex) { log.LogWarning(ex, "Could not delete {What} of a deleted series", what); }
        }

        foreach (var id in found)
        {
            await TryAsync(() => storage.Media.DeletePrefixAsync($"s/{id}/", CancellationToken.None), $"s/{id}/");
            await TryAsync(() => storage.Raw.DeletePrefixAsync($"uploads/{id}/", CancellationToken.None), $"uploads/{id}/");
        }
        // Posters and covers carry the series id in their name; anything else is not this series' own file.
        var images = episodes.Select(e => (e.SeriesId, Key: e.PosterKey)).Concat(covers.Select(c => (SeriesId: c.Id, Key: (string?)c.Key)))
            .Where(x => x.Key is not null && StorageKeys.IsValid(x.Key)
                && (x.Key.StartsWith($"p/posters/{x.SeriesId}-", StringComparison.Ordinal) || x.Key.StartsWith($"p/covers/{x.SeriesId}-", StringComparison.Ordinal)))
            .Select(x => x.Key!).Distinct().ToList();
        foreach (var key in images) await TryAsync(() => storage.Media.DeleteAsync(key, CancellationToken.None), key);

        // Originals outside uploads/{id}/ (the bulk import's incoming/ folder) go only when no other episode uses them.
        var others = episodes.Select(e => e.SourceKey)
            .Where(k => k is not null && !CosKeys.IsCosKey(k) && StorageKeys.IsValid(k) && !k.StartsWith("uploads/", StringComparison.Ordinal))
            .Select(k => k!).Distinct().ToList();
        if (others.Count > 0)
        {
            var stillUsed = await db.Episodes.AsNoTracking().Where(e => e.SourceKey != null && others.Contains(e.SourceKey))
                .Select(e => e.SourceKey!).Distinct().ToListAsync(CancellationToken.None);
            foreach (var key in others.Except(stillUsed)) await TryAsync(() => storage.Raw.DeleteAsync(key, CancellationToken.None), key);
        }

        // COS videos, unless another episode (in a series that stays) uses the same file.
        var fromCos = await CosCleanup.DeleteUnusedAsync(db, cos, episodes.SelectMany(e => new[] { e.SourceKey, e.VideoKey }), log, CancellationToken.None);

        return TypedResults.Ok(new DeleteSeriesResult(found.Count, found, fromCos.Deleted, fromCos.Failed, fromCos.Kept));
    }
}
