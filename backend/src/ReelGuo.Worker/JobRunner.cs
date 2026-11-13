using Microsoft.EntityFrameworkCore;
using ReelGuo.Data;
using ReelGuo.Data.Catalog;
using ReelGuo.Data.Entities;
using ReelGuo.Data.Jobs;
using ReelGuo.Data.Telemetry;
using ReelGuo.Worker.Media;

namespace ReelGuo.Worker;

public sealed class WorkerOptions
{
    public const string Section = "Worker";

    /// <summary>How many episodes to encode at once. Encoding is CPU-heavy; raise only with spare cores.</summary>
    public int Concurrency { get; set; } = 1;

    /// <summary>How often to look for work when the queue is empty.</summary>
    public int PollSeconds { get; set; } = 2;

    /// <summary>How often rankings and hot scores are recomputed.</summary>
    public int RankingsMinutes { get; set; } = 10;
}

/// <summary>Claims media jobs from Postgres and runs them.</summary>
public sealed class JobRunner(
    IServiceScopeFactory scopes,
    Transcoder transcoder,
    WorkerOptions options,
    WorkerStats stats,
    TimeProvider time,
    ILogger<JobRunner> logger) : BackgroundService
{
    private readonly string _workerId = stats.WorkerId;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Worker {WorkerId} started with {Concurrency} slot(s)", _workerId, options.Concurrency);
        return Task.WhenAll(Enumerable.Range(0, Math.Max(1, options.Concurrency)).Select(slot => LoopAsync(slot, stoppingToken)));
    }

    private async Task LoopAsync(int slot, CancellationToken stoppingToken)
    {
        var idle = TimeSpan.FromSeconds(Math.Max(1, options.PollSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await RunOneAsync($"{_workerId}#{slot}", stoppingToken))
                    await Task.Delay(idle, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Database unavailable or similar: back off and keep the worker alive.
                logger.LogError(ex, "Job loop error");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    /// <summary>Runs one job if any is waiting. Returns false when the queue was empty.</summary>
    public async Task<bool> RunOneAsync(string workerId, CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelGuoDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<MediaJobQueue>();

        var job = await queue.ClaimNextAsync(workerId, stoppingToken);
        if (job is null) return false;

        using var activity = ReelGuoTelemetry.Source.StartActivity("media_job");
        activity?.SetTag("reelguo.job_id", job.Id);
        activity?.SetTag("reelguo.episode_id", job.EpisodeId);
        activity?.SetTag("reelguo.attempt", job.Attempts);
        stats.Started(job.Id);
        var started = time.GetTimestamp();

        var episode = await db.Episodes.AsNoTracking().FirstOrDefaultAsync(e => e.Id == job.EpisodeId, stoppingToken);
        if (episode?.SourceKey is null)
        {
            await queue.FailAsync(job, "Episode or source no longer exists", retryable: false, stoppingToken);
            stats.Finished(succeeded: false);
            ReelGuoTelemetry.MediaJobs.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
            return true;
        }

        logger.LogInformation("Job {JobId}: transcoding episode {EpisodeId} (series {SeriesId} ep {Ep}), attempt {Attempt}",
            job.Id, episode.Id, episode.SeriesId, episode.EpisodeNumber, job.Attempts);

        try
        {
            var version = episode.MediaVersion + 1;
            var result = await transcoder.RunAsync(episode.SourceKey, episode.SeriesId, episode.EpisodeNumber, version,
                pct => SetProgressAsync(db, episode.Id, pct, stoppingToken), stoppingToken);

            var now = time.GetUtcNow();
            await db.Episodes.Where(e => e.Id == episode.Id).ExecuteUpdateAsync(u => u
                .SetProperty(e => e.Status, EpisodeStatus.Ready)
                .SetProperty(e => e.HlsPrefix, result.HlsPrefix)
                .SetProperty(e => e.MediaVersion, result.Version)
                .SetProperty(e => e.DurationSeconds, result.DurationSeconds)
                .SetProperty(e => e.Width, result.Width)
                .SetProperty(e => e.Height, result.Height)
                .SetProperty(e => e.PosterKey, result.PosterKey)
                .SetProperty(e => e.RenditionsJson, result.RenditionsJson)
                .SetProperty(e => e.ProcessingProgress, 100)
                .SetProperty(e => e.ProcessingError, (string?)null)
                .SetProperty(e => e.UpdatedAt, now), stoppingToken);

            // A series without a cover uses its first ready episode's poster.
            await db.Series.Where(s => s.Id == episode.SeriesId && s.CoverKey == null)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.CoverKey, result.PosterKey), stoppingToken);
            await SeriesStats.RefreshAsync(db, episode.SeriesId, stoppingToken);
            await queue.CompleteAsync(job.Id, stoppingToken);
            stats.Finished(succeeded: true);
            ReelGuoTelemetry.MediaJobs.Add(1, new KeyValuePair<string, object?>("outcome", "succeeded"));
            ReelGuoTelemetry.TranscodeSeconds.Record(time.GetElapsedTime(started).TotalSeconds);
            logger.LogInformation("Job {JobId}: episode {EpisodeId} ready at {Prefix}", job.Id, episode.Id, result.HlsPrefix);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: hand the job back so another worker (or this one after restart) picks it up.
            await queue.FailAsync(job, "Worker stopped during processing", retryable: true, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            var rejected = ex is MediaRejectedException;
            var final = await queue.FailAsync(job, ex.Message, retryable: !rejected, stoppingToken);
            logger.LogWarning(ex, "Job {JobId} failed (final: {Final})", job.Id, final);
            stats.Finished(succeeded: false);
            ReelGuoTelemetry.MediaJobs.Add(1, new KeyValuePair<string, object?>("outcome", final ? "failed" : "retry"));
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            if (final)
            {
                var message = rejected ? ex.Message : "视频处理失败，请检查文件后重试";
                // A ready episode whose replacement failed keeps playing its current version.
                await db.Episodes.Where(e => e.Id == episode.Id).ExecuteUpdateAsync(u => u
                    .SetProperty(e => e.Status, e => e.Status == EpisodeStatus.Ready ? EpisodeStatus.Ready : EpisodeStatus.Failed)
                    .SetProperty(e => e.ProcessingError, message)
                    .SetProperty(e => e.UpdatedAt, time.GetUtcNow()), stoppingToken);
            }
        }
        return true;
    }

    private static Task SetProgressAsync(ReelGuoDbContext db, long episodeId, int pct, CancellationToken ct) =>
        db.Episodes.Where(e => e.Id == episodeId).ExecuteUpdateAsync(u => u.SetProperty(e => e.ProcessingProgress, pct), ct);
}
