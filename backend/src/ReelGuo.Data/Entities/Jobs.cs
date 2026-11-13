namespace ReelGuo.Data.Entities;

public enum MediaJobKind
{
    Transcode,
}

public enum MediaJobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
}

/// <summary>
/// A unit of background media work, claimed by workers with FOR UPDATE SKIP LOCKED.
/// A Running job whose lock is older than the stale timeout is reclaimed (the worker died).
/// </summary>
public sealed class MediaJob
{
    public long Id { get; set; }
    public MediaJobKind Kind { get; set; }
    public MediaJobState State { get; set; }
    public long EpisodeId { get; set; }
    public Episode Episode { get; set; } = null!;

    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 3;
    /// <summary>Not claimed before this time; used for retry back-off.</summary>
    public DateTimeOffset RunAfter { get; set; }
    public string? LockedBy { get; set; }
    public DateTimeOffset? LockedAt { get; set; }
    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
