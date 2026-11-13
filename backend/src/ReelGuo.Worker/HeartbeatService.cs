using Microsoft.EntityFrameworkCore;
using ReelGuo.Data;
using ReelGuo.Data.Telemetry;

namespace ReelGuo.Worker;

/// <summary>Counters the job runner updates and the heartbeat reports.</summary>
public sealed class WorkerStats
{
    public string WorkerId { get; } = $"{Environment.MachineName}:{Environment.ProcessId}";
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    private long _succeeded;
    private long _failed;
    private long _current;

    public long Succeeded => Interlocked.Read(ref _succeeded);
    public long Failed => Interlocked.Read(ref _failed);
    public long? CurrentJobId => Interlocked.Read(ref _current) is var id && id > 0 ? id : null;

    public void Started(long jobId) => Interlocked.Exchange(ref _current, jobId);
    public void Finished(bool succeeded)
    {
        Interlocked.Exchange(ref _current, 0);
        if (succeeded) Interlocked.Increment(ref _succeeded);
        else Interlocked.Increment(ref _failed);
    }
}

/// <summary>
/// Writes this worker's heartbeat every 30 s (the admin status page and /health/worker read it) and removes
/// heartbeats of workers gone for more than a day.
/// </summary>
public sealed class HeartbeatService(IServiceScopeFactory scopes, WorkerStats stats, TimeProvider time, ILogger<HeartbeatService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await BeatAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Heartbeat failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task BeatAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelGuoDbContext>();
        var now = time.GetUtcNow();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO worker_heartbeats (worker_id, version, started_at, seen_at, jobs_succeeded, jobs_failed, current_job_id)
            VALUES ({stats.WorkerId}, {ReelGuoTelemetry.Version}, {stats.StartedAt}, {now}, {stats.Succeeded}, {stats.Failed}, {stats.CurrentJobId})
            ON CONFLICT (worker_id) DO UPDATE SET
                version = EXCLUDED.version, seen_at = EXCLUDED.seen_at, jobs_succeeded = EXCLUDED.jobs_succeeded,
                jobs_failed = EXCLUDED.jobs_failed, current_job_id = EXCLUDED.current_job_id
            """, ct);
        var stale = now.AddDays(-1);
        await db.WorkerHeartbeats.Where(h => h.SeenAt < stale).ExecuteDeleteAsync(ct);
    }
}
