using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ReelGuo.Data;
using ReelGuo.Data.Entities;

namespace ReelGuo.Api.Social;

public sealed class ModerationOptions
{
    public const string Section = "Moderation";

    /// <summary>
    /// Off by default (as in the reference app, every comment waits for review). When on, comments from
    /// accounts older than 7 days, without links, are published immediately.
    /// </summary>
    public bool AutoApproveTrustedUsers { get; set; }

    /// <summary>Per-account limit: comments per 10 minutes.</summary>
    public int MaxCommentsPer10Minutes { get; set; } = 10;
}

/// <summary>Decides the initial status of a new comment and keeps comment counters correct.</summary>
public sealed partial class Moderation(ReelGuoDbContext db, TimeProvider time)
{
    /// <summary>ModeratedBy value for decisions made by the term filter.</summary>
    public const string SystemActor = "system";

    private static (DateTimeOffset At, string[] Terms)? _cache;
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    public static void InvalidateTerms() => _cache = null;

    private async Task<string[]> TermsAsync(CancellationToken ct)
    {
        if (_cache is { } c && time.GetUtcNow() - c.At < CacheFor) return c.Terms;
        var terms = await db.ModerationTerms.AsNoTracking().Select(t => t.Term).ToArrayAsync(ct);
        _cache = (time.GetUtcNow(), terms);
        return terms;
    }

    [GeneratedRegex(@"(https?://|www\.|\.(com|net|org|cn|io|me|cc|xyz)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();

    /// <summary>
    /// Listed terms → Rejected silently (the author keeps seeing it as "审核中", so spammers learn nothing).
    /// Otherwise Pending, unless trusted auto-approval is on and the comment has no links.
    /// </summary>
    public async Task<(CommentStatus Status, string? Reason)> InitialStatusAsync(string text, User author, ModerationOptions options, CancellationToken ct)
    {
        var lowered = text.ToLowerInvariant();
        var hit = (await TermsAsync(ct)).FirstOrDefault(t => lowered.Contains(t.ToLowerInvariant(), StringComparison.Ordinal));
        if (hit is not null) return (CommentStatus.Rejected, $"命中敏感词：{hit}");
        if (options.AutoApproveTrustedUsers && time.GetUtcNow() - author.CreatedAt > TimeSpan.FromDays(7) && !LinkPattern().IsMatch(text))
            return (CommentStatus.Approved, null);
        return (CommentStatus.Pending, null);
    }

    /// <summary>Recounts approved comments on the target (and replies on the parent) after any status change.</summary>
    public async Task RecountAsync(CommentTarget target, long targetId, long? parentId, CancellationToken ct)
    {
        var approved = await db.Comments.CountAsync(c => c.TargetType == target && c.TargetId == targetId && c.Status == CommentStatus.Approved, ct);
        if (target == CommentTarget.Series)
            await db.Series.Where(s => s.Id == targetId).ExecuteUpdateAsync(u => u.SetProperty(s => s.CommentCount, approved), ct);
        else
            await db.Posts.Where(p => p.Id == targetId).ExecuteUpdateAsync(u => u.SetProperty(p => p.CommentCount, approved), ct);

        if (parentId is { } pid)
        {
            var replies = await db.Comments.CountAsync(c => c.ParentId == pid && c.Status == CommentStatus.Approved, ct);
            await db.Comments.Where(c => c.Id == pid).ExecuteUpdateAsync(u => u.SetProperty(c => c.ReplyCount, replies), ct);
        }
    }
}
