namespace ReelGuo.Data.Entities;

/// <summary>How a country's visitors prove they are adults.</summary>
public enum AgeCheckMode
{
    /// <summary>The 18+ confirmation on first visit. Applies to every country without a rule.</summary>
    Declaration,
    /// <summary>
    /// Verified age required before any content is shown: a signed-in account with a verified age. Until an
    /// age-verification provider is connected, only accounts the team marks as verified can watch.
    /// </summary>
    Strict,
}

/// <summary>Per-country age-verification rule, set by the team after legal review (like the block list).</summary>
public sealed class AgeRule
{
    public required string CountryCode { get; set; }
    public AgeCheckMode Mode { get; set; }
    /// <summary>Why, e.g. the law or the legal review reference.</summary>
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public required string UpdatedBy { get; set; }
}

/// <summary>Written by each worker process every 30 s, so the status page and health checks can see it is alive.</summary>
public sealed class WorkerHeartbeat
{
    /// <summary>Machine name and process id.</summary>
    public required string WorkerId { get; set; }
    public required string Version { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset SeenAt { get; set; }
    public long JobsSucceeded { get; set; }
    public long JobsFailed { get; set; }
    /// <summary>The media job being processed right now, if any.</summary>
    public long? CurrentJobId { get; set; }
}

public enum PlaybackDevice
{
    Mobile,
    Desktop,
}

/// <summary>
/// One play of one episode as experienced by the viewer (quality of experience), sent by the app when the viewer
/// leaves the episode. Anonymous like <see cref="PlayEvent"/> and kept 30 days.
/// </summary>
public sealed class PlaybackSample
{
    public long Id { get; set; }
    public required string SessionId { get; set; }
    public long SeriesId { get; set; }
    public int EpisodeNumber { get; set; }
    public required string Country { get; set; }
    public PlaybackDevice Device { get; set; }
    /// <summary>Browser family: chrome, safari, firefox, edge, samsung, other.</summary>
    public required string Browser { get; set; }
    /// <summary>Time from pressing play (or swiping in) to the first frame; null when it never started.</summary>
    public int? StartupMs { get; set; }
    public int RebufferCount { get; set; }
    public int RebufferMs { get; set; }
    public double WatchedSeconds { get; set; }
    /// <summary>Highest rendition height reached (e.g. 720).</summary>
    public int? MaxHeight { get; set; }
    /// <summary>Fatal error code (hls.js details or media error), null when playback worked.</summary>
    public string? Error { get; set; }
    public DateTimeOffset At { get; set; }
}
