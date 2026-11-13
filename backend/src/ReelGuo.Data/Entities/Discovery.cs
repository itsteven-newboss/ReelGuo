namespace ReelGuo.Data.Entities;

public enum PlayEventKind
{
    /// <summary>An episode started playing (first frame).</summary>
    Start,
    /// <summary>An episode was watched to (nearly) the end.</summary>
    Complete,
}

/// <summary>
/// Anonymous playback signal used for rankings. Keyed by a random per-browser-session id, never by user.
/// Kept 30 days, then deleted by the rankings job.
/// </summary>
public sealed class PlayEvent
{
    public long Id { get; set; }
    public long SeriesId { get; set; }
    public int EpisodeNumber { get; set; }
    public PlayEventKind Kind { get; set; }
    public required string SessionId { get; set; }
    public required string Country { get; set; }
    public DateTimeOffset At { get; set; }
}

public enum RankKind
{
    /// <summary>热播榜: most watched over 7 days.</summary>
    Hot,
    /// <summary>推荐榜: editor picks first, then the best completion rates.</summary>
    Recommend,
    /// <summary>潜力榜: newer series growing fastest.</summary>
    Potential,
}

/// <summary>One row of a precomputed ranking (recomputed every 10 minutes by the worker).</summary>
public sealed class RankEntry
{
    public RankKind Kind { get; set; }
    public int Position { get; set; }
    public long SeriesId { get; set; }
    public Series Series { get; set; } = null!;
    public double MetricValue { get; set; }
    public DateTimeOffset ComputedAt { get; set; }
}

public enum PostStatus
{
    Draft,
    Published,
    Hidden,
}

/// <summary>A 吃瓜 article. Written in Markdown in admin; the sanitised HTML is stored for display.</summary>
public sealed class Post
{
    public long Id { get; set; }
    public required string Title { get; set; }
    public string Summary { get; set; } = "";
    public string ContentMarkdown { get; set; } = "";
    public string ContentHtml { get; set; } = "";
    public string? CoverKey { get; set; }
    public List<string> Tags { get; set; } = [];
    /// <summary>Optional linked series ("观看正片").</summary>
    public long? SeriesId { get; set; }
    public Series? Series { get; set; }
    public PostStatus Status { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public long ViewCount { get; set; }
    public long LikeCount { get; set; }
    public long CollectCount { get; set; }
    public long CommentCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
