namespace ReelGuo.Data.Entities;

/// <summary>
/// One refresh token. Tokens rotate on every use; all tokens from one sign-in share a family, and
/// presenting an already-rotated token revokes the whole family (it was probably stolen).
/// Only a SHA-256 hash of the token is stored.
/// </summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; }
    public long UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid FamilyId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedById { get; set; }
    public string? UserAgent { get; set; }
}

public sealed class SeriesLike
{
    public long UserId { get; set; }
    public long SeriesId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SeriesCollect
{
    public long UserId { get; set; }
    public long SeriesId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Follow
{
    public long FollowerId { get; set; }
    public long FolloweeId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PostLike
{
    public long UserId { get; set; }
    public long PostId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PostCollect
{
    public long UserId { get; set; }
    public long PostId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Where a signed-in viewer stopped in a series (synced from the device).</summary>
public sealed class WatchHistoryEntry
{
    public long UserId { get; set; }
    public long SeriesId { get; set; }
    public int EpisodeNumber { get; set; }
    public double PositionSeconds { get; set; }
    public double DurationSeconds { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum CommentTarget
{
    Series,
    Post,
}

public enum CommentStatus
{
    /// <summary>Waiting for review; visible only to its author.</summary>
    Pending,
    Approved,
    Rejected,
}

public sealed class Comment
{
    public long Id { get; set; }
    public CommentTarget TargetType { get; set; }
    public long TargetId { get; set; }
    /// <summary>Episode for series comments; 0 for posts.</summary>
    public int EpisodeNumber { get; set; }
    public long UserId { get; set; }
    public User User { get; set; } = null!;
    /// <summary>Top-level comment this replies to (replies are one level deep).</summary>
    public long? ParentId { get; set; }
    public required string Text { get; set; }
    public CommentStatus Status { get; set; }
    public long LikeCount { get; set; }
    public int ReplyCount { get; set; }
    public bool Pinned { get; set; }
    /// <summary>Country code at posting time, shown as the region ("IP属地").</summary>
    public required string Country { get; set; }
    public string? RejectReason { get; set; }
    public string? ModeratedBy { get; set; }
    public DateTimeOffset? ModeratedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class CommentLike
{
    public long UserId { get; set; }
    public long CommentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum ReportTarget
{
    Series,
    Post,
    Comment,
    User,
}

public enum ReportReason
{
    /// <summary>Content involving minors. Always urgent.</summary>
    Minor,
    /// <summary>A real person shown without consent (including face swaps). Always urgent.</summary>
    NonConsensual,
    Copyright,
    Illegal,
    Spam,
    Other,
}

public enum ReportStatus
{
    Open,
    Resolved,
    Dismissed,
}

/// <summary>A user or anonymous report. Anonymous reports are allowed so anyone can request a takedown.</summary>
public sealed class Report
{
    public long Id { get; set; }
    public ReportTarget TargetType { get; set; }
    public long TargetId { get; set; }
    public ReportReason Reason { get; set; }
    public bool Urgent { get; set; }
    public string Detail { get; set; } = "";
    public long? ReporterId { get; set; }
    public required string Country { get; set; }
    public ReportStatus Status { get; set; }
    public string? ResolutionNote { get; set; }
    public string? ResolvedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}

/// <summary>A term that makes a comment silently rejected (managed in admin).</summary>
public sealed class ModerationTerm
{
    public int Id { get; set; }
    public required string Term { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string CreatedBy { get; set; }
}
