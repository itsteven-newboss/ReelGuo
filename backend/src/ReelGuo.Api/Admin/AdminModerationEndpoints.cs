using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Api.Media;
using ReelGuo.Api.Social;
using ReelGuo.Api.Users;
using ReelGuo.Data;
using ReelGuo.Data.Entities;

namespace ReelGuo.Api.Admin;

public sealed record AdminCommentDto(
    long Id, CommentTarget TargetType, long TargetId, string TargetTitle, int EpisodeNumber, long? ParentId,
    long UserId, string Username, string Nickname, string Text, CommentStatus Status, string? RejectReason,
    string Country, bool Pinned, long LikeCount, DateTimeOffset CreatedAt, string? ModeratedBy, DateTimeOffset? ModeratedAt);

public sealed record AdminCommentPage(List<AdminCommentDto> Items, int Page, int Size, int Total);

public enum CommentDecision
{
    Approve,
    Reject,
}

public sealed record ModerateCommentsRequest(List<long> Ids, CommentDecision Decision, string? Reason);

public sealed record ModerateCommentsResult(int Updated);

public sealed record PinCommentRequest(bool Pinned);

public sealed record ModerationTermsDto(List<string> Terms);

public sealed record ModerationSummaryDto(int PendingComments, int OpenReports, int UrgentReports);

public sealed record AdminReportDto(
    long Id, ReportTarget TargetType, long TargetId, string TargetSummary, ReportReason Reason, bool Urgent, string Detail,
    long? ReporterId, string? ReporterName, string Country, ReportStatus Status, string? ResolutionNote, string? ResolvedBy,
    DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt, int OpenOnTarget);

public sealed record AdminReportPage(List<AdminReportDto> Items, int Page, int Size, int Total);

public enum ReportResolution
{
    /// <summary>No action needed; only this report is closed.</summary>
    Dismiss,
    /// <summary>Hide the series/post, reject the comment or disable the account; closes every open report on it.</summary>
    Takedown,
}

public sealed record ResolveReportRequest(ReportResolution Action, string? Note);

public sealed record ResolveReportResult(int Closed);

public sealed record AdminMemberDto(
    long Id, string Username, string Nickname, string AvatarColor, UserStatus Status, DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt, DateTimeOffset? TermsAcceptedAt, int CommentCount, int ReportCount, DateTimeOffset? AgeVerifiedAt);

public sealed record AdminMemberPage(List<AdminMemberDto> Items, int Page, int Size, int Total);

public sealed record SetMemberStatusRequest(bool Active, string? Note);

public static class AdminModerationEndpoints
{
    private const int PageSize = 30;
    public const int MaxTerms = 2000;

    public static RouteGroupBuilder MapAdminModeration(this RouteGroupBuilder admin)
    {
        var comments = admin.MapGroup("/comments").WithTags("Admin: moderation");
        comments.MapGet("/", ListComments).WithSummary("Comment review queue. status = pending (default) | approved | rejected.");
        comments.MapPost("/moderate", Moderate).WithSummary("Approves or rejects comments in bulk.");
        comments.MapPost("/{id:long}/pin", Pin).WithSummary("Pins or unpins an approved top-level comment.");

        var moderation = admin.MapGroup("/moderation").WithTags("Admin: moderation");
        moderation.MapGet("/terms", GetTerms).WithSummary("Sensitive terms: matching comments are rejected without telling the author.");
        moderation.MapPut("/terms", PutTerms).WithSummary("Replaces the sensitive-term list.");
        moderation.MapGet("/summary", Summary).WithSummary("Counts for the admin nav badges.");

        var reports = admin.MapGroup("/reports").WithTags("Admin: reports");
        reports.MapGet("/", ListReports).WithSummary("Reports, urgent first. status = open (default) | resolved | dismissed.");
        reports.MapPost("/{id:long}/resolve", Resolve).WithSummary("Dismisses a report, or takes the content down and closes all its open reports.");

        var members = admin.MapGroup("/members").WithTags("Admin: members");
        members.MapGet("/", ListMembers).WithSummary("Viewer accounts. q matches username or nickname.");
        members.MapPost("/{id:long}/status", SetMemberStatus).WithSummary("Enables or disables a viewer account. Disabling signs it out everywhere.");
        return admin;
    }

    private static T? ParseEnum<T>(string? value) where T : struct, Enum =>
        Enum.TryParse<T>((value ?? "").Replace("_", ""), ignoreCase: true, out var parsed) ? parsed : null;

    // ---------- comments ----------

    private static async Task<Ok<AdminCommentPage>> ListComments(
        ReelGuoDbContext db, [FromQuery] string? status, [FromQuery] string? q, [FromQuery] int? page, CancellationToken ct)
    {
        var s = ParseEnum<CommentStatus>(status) ?? CommentStatus.Pending;
        var p = Math.Max(1, page ?? 1);
        var query = db.Comments.AsNoTracking().Where(c => c.Status == s);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(c => EF.Functions.ILike(c.Text, $"%{term}%") || EF.Functions.ILike(c.User.Username, $"%{term}%"));
        }
        var total = await query.CountAsync(ct);
        // The queue is worked oldest first; history views are newest first.
        var ordered = s == CommentStatus.Pending ? query.OrderBy(c => c.CreatedAt) : query.OrderByDescending(c => c.ModeratedAt ?? c.CreatedAt);
        var rows = await ordered.Skip((p - 1) * PageSize).Take(PageSize)
            .Select(c => new { c, c.User.Username, c.User.Nickname }).ToListAsync(ct);

        var seriesIds = rows.Where(r => r.c.TargetType == CommentTarget.Series).Select(r => r.c.TargetId).Distinct().ToList();
        var postIds = rows.Where(r => r.c.TargetType == CommentTarget.Post).Select(r => r.c.TargetId).Distinct().ToList();
        var seriesTitles = await db.Series.Where(x => seriesIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Title, ct);
        var postTitles = await db.Posts.Where(x => postIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Title, ct);

        var items = rows.Select(r => new AdminCommentDto(
            r.c.Id, r.c.TargetType, r.c.TargetId,
            (r.c.TargetType == CommentTarget.Series ? seriesTitles : postTitles).GetValueOrDefault(r.c.TargetId, "(已删除)"),
            r.c.EpisodeNumber, r.c.ParentId, r.c.UserId, r.Username, r.Nickname, r.c.Text, r.c.Status, r.c.RejectReason,
            r.c.Country.Trim(), r.c.Pinned, r.c.LikeCount, r.c.CreatedAt, r.c.ModeratedBy, r.c.ModeratedAt)).ToList();
        return TypedResults.Ok(new AdminCommentPage(items, p, PageSize, total));
    }

    private static async Task<Results<Ok<ModerateCommentsResult>, BadRequest<ApiErrorResponse>>> Moderate(
        ModerateCommentsRequest request, HttpContext http, ReelGuoDbContext db, Moderation moderation, TimeProvider time, CancellationToken ct)
    {
        var ids = (request.Ids ?? []).Distinct().ToList();
        if (ids.Count is 0 or > 200) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_ids", "一次可处理 1-200 条评论"));
        var reason = (request.Reason ?? "").Trim();
        if (reason.Length > 200) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_reason", "原因不能超过 200 个字"));

        var target = request.Decision == CommentDecision.Approve ? CommentStatus.Approved : CommentStatus.Rejected;
        var comments = await db.Comments.Where(c => ids.Contains(c.Id) && c.Status != target).ToListAsync(ct);
        var now = time.GetUtcNow();
        var actor = AdminActor.From(http);
        foreach (var c in comments)
        {
            c.Status = target;
            c.RejectReason = target == CommentStatus.Rejected ? (reason.Length > 0 ? reason : "不符合社区规范") : null;
            c.ModeratedBy = actor;
            c.ModeratedAt = now;
        }
        if (comments.Count > 0)
            db.AddAudit(now, actor, target == CommentStatus.Approved ? "comment.approve" : "comment.reject", "comment",
                string.Join(",", comments.Select(c => c.Id)), new { count = comments.Count, reason = target == CommentStatus.Rejected ? reason : null });
        await db.SaveChangesAsync(ct);

        foreach (var g in comments.GroupBy(c => (c.TargetType, c.TargetId, c.ParentId)))
            await moderation.RecountAsync(g.Key.TargetType, g.Key.TargetId, g.Key.ParentId, ct);
        return TypedResults.Ok(new ModerateCommentsResult(comments.Count));
    }

    private static async Task<Results<NoContent, NotFound<ApiErrorResponse>>> Pin(
        long id, PinCommentRequest request, HttpContext http, ReelGuoDbContext db, TimeProvider time, CancellationToken ct)
    {
        var comment = await db.Comments.FirstOrDefaultAsync(c => c.Id == id && c.ParentId == null && c.Status == CommentStatus.Approved, ct);
        if (comment is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "评论不存在或未通过审核"));
        comment.Pinned = request.Pinned;
        db.AddAudit(time, AdminActor.From(http), request.Pinned ? "comment.pin" : "comment.unpin", "comment", id.ToString());
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    // ---------- sensitive terms ----------

    private static async Task<Ok<ModerationTermsDto>> GetTerms(ReelGuoDbContext db, CancellationToken ct) =>
        TypedResults.Ok(new ModerationTermsDto(await db.ModerationTerms.AsNoTracking().OrderBy(t => t.Term).Select(t => t.Term).ToListAsync(ct)));

    private static async Task<Results<Ok<ModerationTermsDto>, BadRequest<ApiErrorResponse>>> PutTerms(
        ModerationTermsDto request, HttpContext http, ReelGuoDbContext db, TimeProvider time, CancellationToken ct)
    {
        var terms = (request.Terms ?? []).Select(t => t.Trim()).Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (terms.Count > MaxTerms) return TypedResults.BadRequest(ApiErrorResponse.Of("too_many_terms", $"最多 {MaxTerms} 个敏感词"));
        if (terms.Any(t => t.Length > 50)) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_term", "单个敏感词不能超过 50 个字"));

        var existing = await db.ModerationTerms.ToListAsync(ct);
        var keep = terms.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = existing.Where(e => !keep.Contains(e.Term)).ToList();
        var have = existing.Select(e => e.Term).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = terms.Where(t => !have.Contains(t)).ToList();

        var now = time.GetUtcNow();
        var actor = AdminActor.From(http);
        db.ModerationTerms.RemoveRange(removed);
        db.ModerationTerms.AddRange(added.Select(t => new ModerationTerm { Term = t, CreatedAt = now, CreatedBy = actor }));
        if (added.Count > 0 || removed.Count > 0)
            db.AddAudit(now, actor, "moderation.terms", "moderation_terms", "list",
                new { added = added.Count, removed = removed.Count, total = terms.Count });
        await db.SaveChangesAsync(ct);
        Moderation.InvalidateTerms();
        return TypedResults.Ok(new ModerationTermsDto(terms.Order(StringComparer.Ordinal).ToList()));
    }

    private static async Task<Ok<ModerationSummaryDto>> Summary(ReelGuoDbContext db, CancellationToken ct) =>
        TypedResults.Ok(new ModerationSummaryDto(
            await db.Comments.CountAsync(c => c.Status == CommentStatus.Pending, ct),
            await db.Reports.CountAsync(r => r.Status == ReportStatus.Open, ct),
            await db.Reports.CountAsync(r => r.Status == ReportStatus.Open && r.Urgent, ct)));

    // ---------- reports ----------

    private static async Task<Ok<AdminReportPage>> ListReports(
        ReelGuoDbContext db, [FromQuery] string? status, [FromQuery] bool? urgent, [FromQuery] int? page, CancellationToken ct)
    {
        var s = ParseEnum<ReportStatus>(status) ?? ReportStatus.Open;
        var p = Math.Max(1, page ?? 1);
        var query = db.Reports.AsNoTracking().Where(r => r.Status == s);
        if (urgent is { } u) query = query.Where(r => r.Urgent == u);
        var total = await query.CountAsync(ct);
        var ordered = s == ReportStatus.Open
            ? query.OrderByDescending(r => r.Urgent).ThenBy(r => r.CreatedAt)
            : query.OrderByDescending(r => r.ResolvedAt);
        var rows = await ordered.Skip((p - 1) * PageSize).Take(PageSize).ToListAsync(ct);

        var summaries = await SummariesAsync(db, rows, ct);
        var reporterIds = rows.Where(r => r.ReporterId != null).Select(r => r.ReporterId!.Value).Distinct().ToList();
        var reporters = await db.Users.Where(x => reporterIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Username, ct);
        var keys = rows.Select(r => new { r.TargetType, r.TargetId }).Distinct().ToList();
        var openCounts = (await db.Reports.Where(r => r.Status == ReportStatus.Open)
                .GroupBy(r => new { r.TargetType, r.TargetId }).Select(g => new { g.Key.TargetType, g.Key.TargetId, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => (x.TargetType, x.TargetId), x => x.Count);

        var items = rows.Select(r => new AdminReportDto(
            r.Id, r.TargetType, r.TargetId, summaries.GetValueOrDefault((r.TargetType, r.TargetId), "(已删除)"), r.Reason, r.Urgent, r.Detail,
            r.ReporterId, r.ReporterId is { } rid ? reporters.GetValueOrDefault(rid) : null, r.Country.Trim(), r.Status,
            r.ResolutionNote, r.ResolvedBy, r.CreatedAt, r.ResolvedAt, openCounts.GetValueOrDefault((r.TargetType, r.TargetId)))).ToList();
        return TypedResults.Ok(new AdminReportPage(items, p, PageSize, total));
    }

    /// <summary>A one-line description of each reported target (title, comment text or account name).</summary>
    private static async Task<Dictionary<(ReportTarget, long), string>> SummariesAsync(ReelGuoDbContext db, List<Report> rows, CancellationToken ct)
    {
        List<long> Ids(ReportTarget t) => rows.Where(r => r.TargetType == t).Select(r => r.TargetId).Distinct().ToList();
        var result = new Dictionary<(ReportTarget, long), string>();
        var series = Ids(ReportTarget.Series);
        foreach (var x in await db.Series.Where(s => series.Contains(s.Id)).Select(s => new { s.Id, s.Title, s.Status }).ToListAsync(ct))
            result[(ReportTarget.Series, x.Id)] = $"{x.Title}（{x.Status}）";
        var posts = Ids(ReportTarget.Post);
        foreach (var x in await db.Posts.Where(s => posts.Contains(s.Id)).Select(s => new { s.Id, s.Title, s.Status }).ToListAsync(ct))
            result[(ReportTarget.Post, x.Id)] = $"{x.Title}（{x.Status}）";
        var comments = Ids(ReportTarget.Comment);
        foreach (var x in await db.Comments.Where(s => comments.Contains(s.Id)).Select(s => new { s.Id, s.Text, s.User.Username }).ToListAsync(ct))
            result[(ReportTarget.Comment, x.Id)] = $"{x.Username}：{(x.Text.Length > 80 ? x.Text[..80] + "…" : x.Text)}";
        var users = Ids(ReportTarget.User);
        foreach (var x in await db.Users.Where(s => users.Contains(s.Id)).Select(s => new { s.Id, s.Username, s.Nickname, s.Status }).ToListAsync(ct))
            result[(ReportTarget.User, x.Id)] = $"{x.Nickname}（@{x.Username}，{x.Status}）";
        return result;
    }

    private static async Task<Results<Ok<ResolveReportResult>, NotFound<ApiErrorResponse>, Conflict<ApiErrorResponse>, BadRequest<ApiErrorResponse>>> Resolve(
        long id, ResolveReportRequest request, HttpContext http, ReelGuoDbContext db, Moderation moderation, UserTokenService tokens,
        IRevokedSeriesStore revoked, TimeProvider time, CancellationToken ct)
    {
        var note = (request.Note ?? "").Trim();
        if (note.Length > 500) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_note", "处理说明不能超过 500 个字"));
        var report = await db.Reports.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (report is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "举报不存在"));
        if (report.Status != ReportStatus.Open) return TypedResults.Conflict(ApiErrorResponse.Of("already_resolved", "该举报已处理"));

        var now = time.GetUtcNow();
        var actor = AdminActor.From(http);
        var targetId = report.TargetId.ToString();

        if (request.Action == ReportResolution.Dismiss)
        {
            report.Status = ReportStatus.Dismissed;
            report.ResolutionNote = note.Length > 0 ? note : null;
            report.ResolvedBy = actor;
            report.ResolvedAt = now;
            db.AddAudit(now, actor, "report.dismiss", "report", id.ToString(), new { target = report.TargetType, target_id = report.TargetId, note });
            await db.SaveChangesAsync(ct);
            return TypedResults.Ok(new ResolveReportResult(1));
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        Comment? comment = null;
        switch (report.TargetType)
        {
            case ReportTarget.Series:
                await db.Series.Where(s => s.Id == report.TargetId && s.Status == SeriesStatus.Published)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, SeriesStatus.Hidden).SetProperty(s => s.UpdatedAt, now), ct);
                break;
            case ReportTarget.Post:
                await db.Posts.Where(p => p.Id == report.TargetId && p.Status == PostStatus.Published)
                    .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PostStatus.Hidden).SetProperty(p => p.UpdatedAt, now), ct);
                break;
            case ReportTarget.Comment:
                comment = await db.Comments.FirstOrDefaultAsync(c => c.Id == report.TargetId, ct);
                if (comment is not null)
                {
                    comment.Status = CommentStatus.Rejected;
                    comment.RejectReason = "举报下架";
                    comment.ModeratedBy = actor;
                    comment.ModeratedAt = now;
                }
                break;
            case ReportTarget.User:
                await db.Users.Where(u => u.Id == report.TargetId && u.Role == UserRole.User)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, UserStatus.Disabled).SetProperty(x => x.UpdatedAt, now), ct);
                await tokens.RevokeAllForUserAsync(report.TargetId, ct);
                break;
        }

        var closed = await db.Reports
            .Where(r => r.TargetType == report.TargetType && r.TargetId == report.TargetId && r.Status == ReportStatus.Open)
            .ExecuteUpdateAsync(u => u
                .SetProperty(r => r.Status, ReportStatus.Resolved)
                .SetProperty(r => r.ResolutionNote, note.Length > 0 ? note : "已下架")
                .SetProperty(r => r.ResolvedBy, actor)
                .SetProperty(r => r.ResolvedAt, now), ct);
        db.AddAudit(now, actor, "report.takedown", report.TargetType.ToString().ToLowerInvariant(), targetId,
            new { report_id = id, reason = report.Reason, closed, note });
        await db.SaveChangesAsync(ct);
        if (comment is not null) await moderation.RecountAsync(comment.TargetType, comment.TargetId, comment.ParentId, ct);
        await tx.CommitAsync(ct);
        if (report.TargetType == ReportTarget.Series) revoked.Invalidate();
        return TypedResults.Ok(new ResolveReportResult(closed));
    }

    // ---------- members ----------

    private static async Task<Ok<AdminMemberPage>> ListMembers(
        ReelGuoDbContext db, [FromQuery] string? q, [FromQuery] string? status, [FromQuery] int? page, CancellationToken ct)
    {
        var p = Math.Max(1, page ?? 1);
        var query = db.Users.AsNoTracking().Where(u => u.Role == UserRole.User);
        if (ParseEnum<UserStatus>(status) is { } s) query = query.Where(u => u.Status == s);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(u => EF.Functions.ILike(u.Username, $"%{term}%") || EF.Functions.ILike(u.Nickname, $"%{term}%"));
        }
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(u => u.CreatedAt).Skip((p - 1) * PageSize).Take(PageSize)
            .Select(u => new AdminMemberDto(
                u.Id, u.Username, u.Nickname, u.AvatarColor, u.Status, u.CreatedAt, u.LastLoginAt, u.TermsAcceptedAt,
                db.Comments.Count(c => c.UserId == u.Id),
                db.Reports.Count(r => r.TargetType == ReportTarget.User && r.TargetId == u.Id),
                u.AgeVerifiedAt))
            .ToListAsync(ct);
        return TypedResults.Ok(new AdminMemberPage(items, p, PageSize, total));
    }

    private static async Task<Results<NoContent, NotFound<ApiErrorResponse>>> SetMemberStatus(
        long id, SetMemberStatusRequest request, HttpContext http, ReelGuoDbContext db, UserTokenService tokens, TimeProvider time, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.Role == UserRole.User, ct);
        if (user is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "会员不存在"));
        var next = request.Active ? UserStatus.Active : UserStatus.Disabled;
        if (user.Status == next) return TypedResults.NoContent();

        var now = time.GetUtcNow();
        user.Status = next;
        user.UpdatedAt = now;
        db.AddAudit(now, AdminActor.From(http), request.Active ? "member.enable" : "member.disable", "user", user.Username,
            new { id, note = (request.Note ?? "").Trim() });
        await db.SaveChangesAsync(ct);
        if (!request.Active) await tokens.RevokeAllForUserAsync(id, ct);
        return TypedResults.NoContent();
    }
}
