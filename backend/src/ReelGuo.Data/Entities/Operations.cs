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

/// <summary>
/// One span of a stored trace (Telemetry/LocalTelemetry.cs): a request, a worker job, or a step inside one (a database
/// command, an outgoing HTTP call). Only interesting traces are kept: failed, slow, or a small sample of the rest.
/// </summary>
public sealed class TelemetrySpan
{
    public long Id { get; set; }
    /// <summary>W3C trace id, 32 hex characters.</summary>
    public string TraceId { get; set; } = "";
    /// <summary>16 hex characters.</summary>
    public string SpanId { get; set; } = "";
    public string? ParentSpanId { get; set; }
    /// <summary>True for the first span of the trace in its process (an incoming request, a worker job).</summary>
    public bool IsRoot { get; set; }
    /// <summary>reelguo-api or reelguo-worker.</summary>
    public string Service { get; set; } = "";
    /// <summary>e.g. "GET /api/v1/series/{id}", "media.transcode", "postgresql".</summary>
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public double DurationMs { get; set; }
    public bool IsError { get; set; }
    /// <summary>The error message, when the span failed.</summary>
    public string? StatusMessage { get; set; }
    /// <summary>The span's attributes (route, status code, SQL, exception), values cut to a few hundred characters; JSON.</summary>
    public string Attributes { get; set; } = "{}";
}

/// <summary>A warning or error log line, with the trace it happened in (when there was one).</summary>
public sealed class TelemetryLog
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public string Service { get; set; } = "";
    /// <summary>Microsoft.Extensions.Logging level: 3 warning, 4 error, 5 critical.</summary>
    public int Level { get; set; }
    public string Category { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Exception { get; set; }
    public string? TraceId { get; set; }
    public string? SpanId { get; set; }
}
