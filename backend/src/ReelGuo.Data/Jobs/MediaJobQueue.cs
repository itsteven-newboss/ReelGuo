using Microsoft.EntityFrameworkCore;
using ReelGuo.Data.Entities;

namespace ReelGuo.Data.Jobs;

/// <summary>Postgres-backed job queue for media work. Safe with several workers (FOR UPDATE SKIP LOCKED).</summary>
public sealed class MediaJobQueue(ReelGuoDbContext db, TimeProvider time)
{
    /// <summary>A Running job whose lock is older than this is assumed orphaned and is claimed again.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    /// <summary>Queues a transcode unless one is already queued or running for the episode. Caller saves changes.</summary>
    public async Task<MediaJob> EnqueueTranscodeAsync(long episodeId, CancellationToken cancellationToken)
    {
        var pending = await db.MediaJobs.FirstOrDefaultAsync(
            j => j.EpisodeId == episodeId && (j.State == MediaJobState.Queued || j.State == MediaJobState.Running),
            cancellationToken);
        if (pending is not null) return pending;

        var now = time.GetUtcNow();
        var job = new MediaJob
        {
            Kind = MediaJobKind.Transcode,
            State = MediaJobState.Queued,
            EpisodeId = episodeId,
            RunAfter = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.MediaJobs.Add(job);
        return job;
    }

    /// <summary>Atomically claims the next runnable job, or returns null.</summary>
    public async Task<MediaJob?> ClaimNextAsync(string workerId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var staleBefore = now - StaleAfter;
        var claimed = await db.MediaJobs.FromSql($"""
            UPDATE media_jobs
               SET state = 'Running', locked_by = {workerId}, locked_at = {now}, attempts = attempts + 1, updated_at = {now}
             WHERE id = (
                   SELECT id FROM media_jobs
                    WHERE (state = 'Queued' AND run_after <= {now})
                       OR (state = 'Running' AND locked_at < {staleBefore})
                    ORDER BY id
                    FOR UPDATE SKIP LOCKED
                    LIMIT 1)
            RETURNING *
            """).AsNoTracking().ToListAsync(cancellationToken);
        return claimed.FirstOrDefault();
    }

    public async Task CompleteAsync(long jobId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await db.MediaJobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.State, MediaJobState.Succeeded)
            .SetProperty(j => j.Error, (string?)null)
            .SetProperty(j => j.UpdatedAt, now), cancellationToken);
    }

    /// <summary>Records a failure; retries with back-off until MaxAttempts. Returns true when the job is finally failed.</summary>
    public async Task<bool> FailAsync(MediaJob job, string error, bool retryable, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var final = !retryable || job.Attempts >= job.MaxAttempts;
        var message = error.Length > 2000 ? error[..2000] : error;
        await db.MediaJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.State, final ? MediaJobState.Failed : MediaJobState.Queued)
            .SetProperty(j => j.RunAfter, now.AddMinutes(job.Attempts))
            .SetProperty(j => j.LockedBy, (string?)null)
            .SetProperty(j => j.Error, message)
            .SetProperty(j => j.UpdatedAt, now), cancellationToken);
        return final;
    }
}
