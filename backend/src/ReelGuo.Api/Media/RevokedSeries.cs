using System.Collections.Frozen;
using Microsoft.EntityFrameworkCore;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Data.Entities;

namespace ReelGuo.Api.Media;

/// <summary>
/// Series whose media must stop playing even through links already handed out (playback links stay valid for hours):
/// series that were published and are now hidden, removed or past their licence end. Both media edges check it, the
/// API's /media and the CDN worker (via /api/edge/config), so a takedown stops playback within the cache time.
/// </summary>
public interface IRevokedSeriesStore
{
    ValueTask<IReadOnlySet<long>> GetAsync(CancellationToken cancellationToken);

    /// <summary>Forces a reload on the next request; called after any change to a series' status.</summary>
    void Invalidate();
}

public sealed class RevokedSeriesStore(IServiceScopeFactory scopes, TimeProvider time, ILogger<RevokedSeriesStore> logger) : IRevokedSeriesStore
{
    private readonly DbSnapshot<FrozenSet<long>> _snapshot = new(scopes, time, logger, TimeSpan.FromSeconds(30), async (db, now, ct) =>
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var ids = await db.Series.AsNoTracking()
            .Where(s => s.PublishedAt != null && (s.Status != SeriesStatus.Published || (s.LicenceEnd != null && s.LicenceEnd < today)))
            .Select(s => s.Id)
            .ToListAsync(ct);
        return ids.ToFrozenSet();
    });

    public async ValueTask<IReadOnlySet<long>> GetAsync(CancellationToken cancellationToken) => await _snapshot.GetAsync(cancellationToken);

    public void Invalidate() => _snapshot.Invalidate();
}
