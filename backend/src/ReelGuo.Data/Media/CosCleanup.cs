using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ReelGuo.Data.Media;

/// <param name="Deleted">Files removed from COS.</param>
/// <param name="Failed">Files COS refused or could not be reached for (logged as warnings, with the key).</param>
/// <param name="Kept">Files still used by another episode, or not deletable because COS is no longer configured.</param>
public sealed record CosCleanupResult(int Deleted, int Failed, int Kept)
{
    public static readonly CosCleanupResult None = new(0, 0, 0);
}

/// <summary>
/// Removes COS videos nothing points at any more, so deleting or replacing content in admin leaves no orphans in the
/// bucket. (The bucket is ReelGuo's own since 2026-11-17; before that it was shared with Viora and nothing was deleted.)
/// Call it after the change is saved: a file any episode still uses, as its source or as what it plays, is kept.
/// </summary>
public static class CosCleanup
{
    private const int Parallelism = 8;

    public static async Task<CosCleanupResult> DeleteUnusedAsync(
        ReelGuoDbContext db, CosVideoStore cos, IEnumerable<string?> keys, ILogger log, CancellationToken ct)
    {
        var candidates = keys.Where(CosKeys.IsCosKey).Select(k => k!).Distinct(StringComparer.Ordinal).ToList();
        if (candidates.Count == 0) return CosCleanupResult.None;
        if (!cos.Enabled)
        {
            log.LogWarning("COS is not configured, so {Count} COS video file(s) of deleted content were left in the bucket", candidates.Count);
            return new(0, 0, candidates.Count);
        }

        // Runs after the admin's change is saved: report problems, never fail the request or the job over them.
        List<string> unused;
        try
        {
            unused = await UnusedAsync(db, candidates, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not check which of {Count} COS video file(s) are still used; none deleted", candidates.Count);
            return new(0, candidates.Count, 0);
        }

        var deleted = 0;
        var failed = 0;
        await Parallel.ForEachAsync(unused, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (key, token) =>
        {
            try
            {
                await cos.DeleteAsync(key, token);
                Interlocked.Increment(ref deleted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref failed);
                log.LogWarning(ex, "Could not delete COS video {Key}; delete it in the COS console", key);
            }
        });
        if (deleted > 0) log.LogInformation("Deleted {Count} unused COS video file(s)", deleted);
        return new(deleted, failed, candidates.Count - unused.Count);
    }

    /// <summary>The candidates no episode uses, as its source or as what it plays.</summary>
    private static async Task<List<string>> UnusedAsync(ReelGuoDbContext db, List<string> candidates, CancellationToken ct)
    {
        var used = await db.Episodes.AsNoTracking()
            .Where(e => (e.SourceKey != null && candidates.Contains(e.SourceKey)) || (e.VideoKey != null && candidates.Contains(e.VideoKey)))
            .Select(e => new { e.SourceKey, e.VideoKey })
            .ToListAsync(ct);
        var stillUsed = used.SelectMany(u => new[] { u.SourceKey, u.VideoKey }).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return candidates.Where(k => !stillUsed.Contains(k)).ToList();
    }
}
