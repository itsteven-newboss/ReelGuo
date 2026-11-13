using ReelGuo.Data;

namespace ReelGuo.Api.Infrastructure;

/// <summary>
/// A small value loaded from the database and cached for a few seconds, for checks that run on every request
/// (revoked series, age rules). If a refresh fails it keeps serving the last value; with none yet, the error
/// propagates so the caller fails closed. <see cref="Invalidate"/> makes this instance reload on its next use.
/// </summary>
public sealed class DbSnapshot<T>(
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger logger,
    TimeSpan cacheFor,
    Func<ReelGuoDbContext, DateTimeOffset, CancellationToken, Task<T>> load)
    where T : class
{
    private sealed record Entry(T Value, DateTimeOffset LoadedAt);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Entry? _entry;

    public async ValueTask<T> GetAsync(CancellationToken cancellationToken)
    {
        if (IsFresh(_entry)) return _entry!.Value;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = _entry;
            if (IsFresh(current)) return current!.Value;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ReelGuoDbContext>();
                var now = time.GetUtcNow();
                var next = new Entry(await load(db, now, cancellationToken), now);
                _entry = next;
                return next.Value;
            }
            catch (Exception ex) when (current is not null && ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not refresh {Snapshot}; keeping the last known value.", typeof(T).Name);
                return current.Value;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate()
    {
        var current = _entry;
        if (current is not null) _entry = current with { LoadedAt = DateTimeOffset.MinValue };
    }

    private bool IsFresh(Entry? e) => e is not null && time.GetUtcNow() - e.LoadedAt < cacheFor;
}
