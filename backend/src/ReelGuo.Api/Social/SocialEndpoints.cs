using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelGuo.Api.Geo;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Api.Public;
using ReelGuo.Api.Users;
using ReelGuo.Data;
using ReelGuo.Data.Entities;
using ReelGuo.Data.Telemetry;

namespace ReelGuo.Api.Social;

public sealed record InteractionDto(bool On, long Count);

public sealed record CommentDto(
    long Id, long UserId, string Nickname, string AvatarColor, string Text, DateTimeOffset CreatedAt, long LikeCount, bool Liked,
    int ReplyCount, List<CommentDto> Replies, bool Pinned, string IpRegion, CommentStatus Status, long? ParentId);

public sealed record CommentPage(int Ep, List<CommentDto> Items, PaginationDto Pagination);

public sealed record CreateCommentRequest(string Text, long? ParentId);

public sealed record CreateReportRequest(ReportTarget TargetType, long TargetId, ReportReason Reason, string? Detail);

public sealed record ReportCreatedDto(long Id, bool Urgent);

public static class SocialEndpoints
{
    public const int MaxCommentLength = 500;

    public static RouteGroupBuilder MapSocial(this RouteGroupBuilder v1)
    {
        var signedIn = v1.MapGroup("/").RequireAuthorization(UserAuth.Policy).WithTags("Social");
        signedIn.MapPut("/series/{id:long}/like", (long id, HttpContext h, ReelGuoDbContext db, TimeProvider t, CancellationToken ct) => SeriesLike(id, true, h, db, t, ct));
        signedIn.MapDelete("/series/{id:long}/like", (long id, HttpContext h, ReelGuoDbContext db, TimeProvider t, CancellationToken ct) => SeriesLike(id, false, h, db, t, ct));
        signedIn.MapPut("/series/{id:long}/collect", (long id, HttpContext h, ReelGuoDbContext db, TimeProvider t, CancellationToken ct) => SeriesCollect(id, true, h, db, t, ct));
        signedIn.MapDelete("/series/{id:long}/collect", (long id, HttpContext h, ReelGuoDbContext db, TimeProvider t, CancellationToken ct) => SeriesCollect(id, false, h, db, t, ct));
        signedIn.MapPut("/users/{id:long}/follow", (long id, HttpContext h, ReelGuoDbContext db, TimeProvider t, CancellationToken ct) => Follow(id, true, h, db, t, ct));
        signedIn.MapDelete("/users/{id:long}/follow", (long id, HttpContext h, ReelGuoDbContext db, TimeProvider t, CancellationToken ct) => Follow(id, false, h, db, t, ct));
        signedIn.MapPut("/posts/{id:long}/like", (long id, HttpContext h, ReelGuoDbContext db, TimeProvider t, CancellationToken ct) => PostLike(id, true, h, db, t, ct));
        signedIn.MapDelete("/posts/{id:long}/like", (long id, HttpContext h, ReelGuoDbContext db, TimeProvider t, CancellationToken ct) => PostLike(id, false, h, db, t, ct));
        signedIn.MapPut("/posts/{id:long}/collect", (long id, HttpContext h, ReelGuoDbContext db, TimeProvider t, CancellationToken ct) => PostCollect(id, true, h, db, t, ct));
        signedIn.MapDelete("/posts/{id:long}/collect", (long id, HttpContext h, ReelGuoDbContext db, TimeProvider t, CancellationToken ct) => PostCollect(id, false, h, db, t, ct));
        signedIn.MapPut("/comments/{id:long}/like", (long id, HttpContext h, ReelGuoDbContext db, TimeProvider t, CancellationToken ct) => CommentLike(id, true, h, db, t, ct));
        signedIn.MapDelete("/comments/{id:long}/like", (long id, HttpContext h, ReelGuoDbContext db, TimeProvider t, CancellationToken ct) => CommentLike(id, false, h, db, t, ct));
        signedIn.MapDelete("/comments/{id:long}", DeleteOwnComment).WithSummary("Deletes the viewer's own comment.");

        // Reading comments works signed out; posting needs an account.
        var comments = v1.MapGroup("/").WithTags("Comments");
        comments.MapGet("/series/{id:long}/episodes/{ep:int}/comments",
            (long id, int ep, HttpContext h, ReelGuoDbContext db, [FromQuery] int? page, CancellationToken ct) =>
                ListComments(CommentTarget.Series, id, ep, h, db, page, ct));
        comments.MapGet("/posts/{id:long}/comments",
            (long id, HttpContext h, ReelGuoDbContext db, [FromQuery] int? page, CancellationToken ct) => ListComments(CommentTarget.Post, id, 0, h, db, page, ct));
        comments.MapGet("/comments/{id:long}/replies", ListReplies);
        comments.MapPost("/series/{id:long}/episodes/{ep:int}/comments",
            (long id, int ep, CreateCommentRequest r, HttpContext h, ReelGuoDbContext db, Moderation m, IOptions<ModerationOptions> o, ICountryResolver geo, TimeProvider t, CancellationToken ct) =>
                CreateComment(CommentTarget.Series, id, ep, r, h, db, m, o, geo, t, ct))
            .RequireAuthorization(UserAuth.Policy).RequireRateLimiting(RateLimits.Comments)
            .WithSummary("Posts a comment; it waits for review (\"评论已提交，审核通过后展示\").");
        comments.MapPost("/posts/{id:long}/comments",
            (long id, CreateCommentRequest r, HttpContext h, ReelGuoDbContext db, Moderation m, IOptions<ModerationOptions> o, ICountryResolver geo, TimeProvider t, CancellationToken ct) =>
                CreateComment(CommentTarget.Post, id, 0, r, h, db, m, o, geo, t, ct))
            .RequireAuthorization(UserAuth.Policy).RequireRateLimiting(RateLimits.Comments);

        v1.MapPost("/reports", CreateReport).WithTags("Reports").RequireRateLimiting(RateLimits.Reports)
            .WithSummary("Reports content. Works signed out, so anyone can request a takedown.");
        return v1;
    }

    // ---------- likes, favourites, follows ----------

    /// <summary>Insert-or-delete one relation row and adjust its counter in the same transaction (idempotent).</summary>
    private static async Task Toggle(ReelGuoDbContext db, bool on, Func<Task<int>> insert, Func<Task<int>> delete, Func<int, Task> adjust, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var changed = on ? await insert() : await delete();
        if (changed == 1) await adjust(on ? 1 : -1);
        await tx.CommitAsync(ct);
    }

    private static async Task<Results<Ok<InteractionDto>, NotFound<ApiErrorResponse>>> SeriesLike(long id, bool on, HttpContext http, ReelGuoDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (!await db.Series.AnyAsync(s => s.Id == id && s.Status == SeriesStatus.Published, ct)) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "内容不存在"));
        var uid = UserAuth.UserId(http);
        var now = time.GetUtcNow();
        await Toggle(db, on,
            () => db.Database.ExecuteSqlAsync($"INSERT INTO series_likes (user_id, series_id, created_at) VALUES ({uid}, {id}, {now}) ON CONFLICT DO NOTHING", ct),
            () => db.SeriesLikes.Where(x => x.UserId == uid && x.SeriesId == id).ExecuteDeleteAsync(ct),
            d => db.Series.Where(s => s.Id == id).ExecuteUpdateAsync(u => u.SetProperty(s => s.LikeCount, s => Math.Max(0, s.LikeCount + d)), ct), ct);
        return TypedResults.Ok(new InteractionDto(on, await db.Series.Where(s => s.Id == id).Select(s => s.LikeCount).FirstAsync(ct)));
    }

    private static async Task<Results<Ok<InteractionDto>, NotFound<ApiErrorResponse>>> SeriesCollect(long id, bool on, HttpContext http, ReelGuoDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (!await db.Series.AnyAsync(s => s.Id == id && s.Status == SeriesStatus.Published, ct)) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "内容不存在"));
        var uid = UserAuth.UserId(http);
        var now = time.GetUtcNow();
        await Toggle(db, on,
            () => db.Database.ExecuteSqlAsync($"INSERT INTO series_collects (user_id, series_id, created_at) VALUES ({uid}, {id}, {now}) ON CONFLICT DO NOTHING", ct),
            () => db.SeriesCollects.Where(x => x.UserId == uid && x.SeriesId == id).ExecuteDeleteAsync(ct),
            d => db.Series.Where(s => s.Id == id).ExecuteUpdateAsync(u => u.SetProperty(s => s.CollectCount, s => Math.Max(0, s.CollectCount + d)), ct), ct);
        return TypedResults.Ok(new InteractionDto(on, await db.Series.Where(s => s.Id == id).Select(s => s.CollectCount).FirstAsync(ct)));
    }

    private static async Task<Results<Ok<InteractionDto>, NotFound<ApiErrorResponse>, BadRequest<ApiErrorResponse>>> Follow(long id, bool on, HttpContext http, ReelGuoDbContext db, TimeProvider time, CancellationToken ct)
    {
        var uid = UserAuth.UserId(http);
        if (id == uid) return TypedResults.BadRequest(ApiErrorResponse.Of("cannot_follow_self", "不能关注自己"));
        if (!await db.Users.AnyAsync(u => u.Id == id && u.Status == UserStatus.Active && u.Role != UserRole.Admin, ct))
            return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "用户不存在"));
        var now = time.GetUtcNow();
        await Toggle(db, on,
            () => db.Database.ExecuteSqlAsync($"INSERT INTO follows (follower_id, followee_id, created_at) VALUES ({uid}, {id}, {now}) ON CONFLICT DO NOTHING", ct),
            () => db.Follows.Where(x => x.FollowerId == uid && x.FolloweeId == id).ExecuteDeleteAsync(ct),
            _ => Task.CompletedTask, ct);
        return TypedResults.Ok(new InteractionDto(on, await db.Follows.CountAsync(f => f.FolloweeId == id, ct)));
    }

    private static async Task<Results<Ok<InteractionDto>, NotFound<ApiErrorResponse>>> PostLike(long id, bool on, HttpContext http, ReelGuoDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (!await db.Posts.AnyAsync(p => p.Id == id && p.Status == PostStatus.Published, ct)) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "内容不存在"));
        var uid = UserAuth.UserId(http);
        var now = time.GetUtcNow();
        await Toggle(db, on,
            () => db.Database.ExecuteSqlAsync($"INSERT INTO post_likes (user_id, post_id, created_at) VALUES ({uid}, {id}, {now}) ON CONFLICT DO NOTHING", ct),
            () => db.PostLikes.Where(x => x.UserId == uid && x.PostId == id).ExecuteDeleteAsync(ct),
            d => db.Posts.Where(p => p.Id == id).ExecuteUpdateAsync(u => u.SetProperty(p => p.LikeCount, p => Math.Max(0, p.LikeCount + d)), ct), ct);
        return TypedResults.Ok(new InteractionDto(on, await db.Posts.Where(p => p.Id == id).Select(p => p.LikeCount).FirstAsync(ct)));
    }

    private static async Task<Results<Ok<InteractionDto>, NotFound<ApiErrorResponse>>> PostCollect(long id, bool on, HttpContext http, ReelGuoDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (!await db.Posts.AnyAsync(p => p.Id == id && p.Status == PostStatus.Published, ct)) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "内容不存在"));
        var uid = UserAuth.UserId(http);
        var now = time.GetUtcNow();
        await Toggle(db, on,
            () => db.Database.ExecuteSqlAsync($"INSERT INTO post_collects (user_id, post_id, created_at) VALUES ({uid}, {id}, {now}) ON CONFLICT DO NOTHING", ct),
            () => db.PostCollects.Where(x => x.UserId == uid && x.PostId == id).ExecuteDeleteAsync(ct),
            d => db.Posts.Where(p => p.Id == id).ExecuteUpdateAsync(u => u.SetProperty(p => p.CollectCount, p => Math.Max(0, p.CollectCount + d)), ct), ct);
        return TypedResults.Ok(new InteractionDto(on, await db.Posts.Where(p => p.Id == id).Select(p => p.CollectCount).FirstAsync(ct)));
    }

    private static async Task<Results<Ok<InteractionDto>, NotFound<ApiErrorResponse>>> CommentLike(long id, bool on, HttpContext http, ReelGuoDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (!await db.Comments.AnyAsync(c => c.Id == id && c.Status == CommentStatus.Approved, ct)) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "评论不存在"));
        var uid = UserAuth.UserId(http);
        var now = time.GetUtcNow();
        await Toggle(db, on,
            () => db.Database.ExecuteSqlAsync($"INSERT INTO comment_likes (user_id, comment_id, created_at) VALUES ({uid}, {id}, {now}) ON CONFLICT DO NOTHING", ct),
            () => db.CommentLikes.Where(x => x.UserId == uid && x.CommentId == id).ExecuteDeleteAsync(ct),
            d => db.Comments.Where(c => c.Id == id).ExecuteUpdateAsync(u => u.SetProperty(c => c.LikeCount, c => Math.Max(0, c.LikeCount + d)), ct), ct);
        return TypedResults.Ok(new InteractionDto(on, await db.Comments.Where(c => c.Id == id).Select(c => c.LikeCount).FirstAsync(ct)));
    }

    // ---------- comments ----------

    private const int PageSize = 20;
    private const int PreviewReplies = 3;

    /// <summary>
    /// Approved comments, plus the viewer's own pending ones so they can see what they wrote. Comments the term filter
    /// rejected also stay visible to their author (as pending), so spammers can't tell they were filtered.
    /// Comments an admin rejected disappear.
    /// </summary>
    private static IQueryable<Comment> Visible(ReelGuoDbContext db, long? viewer) =>
        db.Comments.AsNoTracking().Where(c => c.Status == CommentStatus.Approved
            || (viewer != null && c.UserId == viewer
                && (c.Status == CommentStatus.Pending || (c.Status == CommentStatus.Rejected && c.ModeratedBy == Moderation.SystemActor))));

    /// <summary>Viewers only ever see approved or pending; a filtered comment is reported as pending.</summary>
    private static CommentDto ToDto(Comment c, HashSet<long> liked, List<CommentDto>? replies = null) => new(
        c.Id, c.UserId, c.User.Nickname, c.User.AvatarColor, c.Text, c.CreatedAt, c.LikeCount, liked.Contains(c.Id),
        c.ReplyCount, replies ?? [], c.Pinned, c.Country.Trim(), c.Status == CommentStatus.Approved ? CommentStatus.Approved : CommentStatus.Pending, c.ParentId);

    private static async Task<HashSet<long>> LikedAsync(ReelGuoDbContext db, long? viewer, IEnumerable<long> ids, CancellationToken ct)
    {
        if (viewer is null) return [];
        var list = ids.ToList();
        return (await db.CommentLikes.Where(l => l.UserId == viewer && list.Contains(l.CommentId)).Select(l => l.CommentId).ToListAsync(ct)).ToHashSet();
    }

    private static async Task<Ok<CommentPage>> ListComments(CommentTarget target, long id, int ep, HttpContext http, ReelGuoDbContext db, int? page, CancellationToken ct)
    {
        var viewer = await UserAuth.OptionalUserIdAsync(http);
        var p = Math.Max(1, page ?? 1);
        var query = Visible(db, viewer).Where(c => c.TargetType == target && c.TargetId == id && c.EpisodeNumber == ep && c.ParentId == null);
        var total = await query.CountAsync(ct);
        var top = await query.Include(c => c.User).OrderByDescending(c => c.Pinned).ThenByDescending(c => c.CreatedAt)
            .Skip((p - 1) * PageSize).Take(PageSize).ToListAsync(ct);

        var topIds = top.Select(c => c.Id).ToList();
        var replies = await Visible(db, viewer).Include(c => c.User).Where(c => c.ParentId != null && topIds.Contains(c.ParentId.Value))
            .OrderBy(c => c.CreatedAt).ToListAsync(ct);
        var liked = await LikedAsync(db, viewer, topIds.Concat(replies.Select(r => r.Id)), ct);
        var byParent = replies.GroupBy(r => r.ParentId!.Value).ToDictionary(g => g.Key, g => g.Take(PreviewReplies).Select(r => ToDto(r, liked)).ToList());

        var items = top.Select(c => ToDto(c, liked, byParent.GetValueOrDefault(c.Id))).ToList();
        return TypedResults.Ok(new CommentPage(ep, items, new PaginationDto(p, Math.Max(1, (int)Math.Ceiling(total / (double)PageSize)), PageSize, total)));
    }

    private static async Task<Ok<CommentPage>> ListReplies(long id, HttpContext http, ReelGuoDbContext db, [FromQuery] int? page, CancellationToken ct)
    {
        var viewer = await UserAuth.OptionalUserIdAsync(http);
        var p = Math.Max(1, page ?? 1);
        var query = Visible(db, viewer).Where(c => c.ParentId == id);
        var total = await query.CountAsync(ct);
        var rows = await query.Include(c => c.User).OrderBy(c => c.CreatedAt).Skip((p - 1) * PageSize).Take(PageSize).ToListAsync(ct);
        var liked = await LikedAsync(db, viewer, rows.Select(r => r.Id), ct);
        return TypedResults.Ok(new CommentPage(0, rows.Select(r => ToDto(r, liked)).ToList(),
            new PaginationDto(p, Math.Max(1, (int)Math.Ceiling(total / (double)PageSize)), PageSize, total)));
    }

    private static async Task<Results<Ok<CommentDto>, BadRequest<ApiErrorResponse>, NotFound<ApiErrorResponse>, JsonHttpResult<ApiErrorResponse>>> CreateComment(
        CommentTarget target, long id, int ep, CreateCommentRequest request, HttpContext http, ReelGuoDbContext db, Moderation moderation,
        IOptions<ModerationOptions> options, ICountryResolver geo, TimeProvider time, CancellationToken ct)
    {
        var text = (request.Text ?? "").Trim();
        if (text.Length is 0 or > MaxCommentLength)
            return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_text", $"评论需为 1-{MaxCommentLength} 个字"));

        var exists = target == CommentTarget.Series
            ? await db.Episodes.AnyAsync(e => e.SeriesId == id && e.EpisodeNumber == ep && e.Status == EpisodeStatus.Ready
                && e.Series.Status == SeriesStatus.Published, ct)
            : await db.Posts.AnyAsync(p => p.Id == id && p.Status == PostStatus.Published, ct);
        if (!exists) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "内容不存在"));

        if (request.ParentId is { } parentId && !await db.Comments.AnyAsync(c => c.Id == parentId && c.ParentId == null
                && c.TargetType == target && c.TargetId == id && c.EpisodeNumber == ep && c.Status == CommentStatus.Approved, ct))
            return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_parent", "回复的评论不存在"));

        var uid = UserAuth.UserId(http);
        var now = time.GetUtcNow();
        var recent = await db.Comments.CountAsync(c => c.UserId == uid && c.CreatedAt > now.AddMinutes(-10), ct);
        if (recent >= options.Value.MaxCommentsPer10Minutes)
            return TypedResults.Json(ApiErrorResponse.Of("comment_rate_limited", "评论太频繁，请稍后再试"), statusCode: StatusCodes.Status429TooManyRequests);

        var author = await db.Users.FirstAsync(u => u.Id == uid, ct);
        var (status, reason) = await moderation.InitialStatusAsync(text, author, options.Value, ct);
        var comment = new Comment
        {
            TargetType = target, TargetId = id, EpisodeNumber = ep, UserId = uid, User = author, ParentId = request.ParentId,
            Text = text, Status = status, RejectReason = reason, Country = geo.Resolve(http), CreatedAt = now,
            ModeratedBy = reason is null ? null : Moderation.SystemActor, ModeratedAt = reason is null ? null : now,
        };
        db.Comments.Add(comment);
        await db.SaveChangesAsync(ct);
        ReelGuoTelemetry.Comments.Add(1, new KeyValuePair<string, object?>("status", status.ToString().ToLowerInvariant()));
        if (status == CommentStatus.Approved) await moderation.RecountAsync(target, id, request.ParentId, ct);

        return TypedResults.Ok(ToDto(comment, []));
    }

    private static async Task<Results<NoContent, NotFound<ApiErrorResponse>>> DeleteOwnComment(long id, HttpContext http, ReelGuoDbContext db, Moderation moderation, CancellationToken ct)
    {
        var uid = UserAuth.UserId(http);
        var comment = await db.Comments.FirstOrDefaultAsync(c => c.Id == id && c.UserId == uid, ct);
        if (comment is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "评论不存在"));
        db.Comments.Remove(comment);
        await db.SaveChangesAsync(ct);
        await moderation.RecountAsync(comment.TargetType, comment.TargetId, comment.ParentId, ct);
        return TypedResults.NoContent();
    }

    // ---------- reports ----------

    private static async Task<Results<Ok<ReportCreatedDto>, BadRequest<ApiErrorResponse>, NotFound<ApiErrorResponse>>> CreateReport(
        CreateReportRequest request, HttpContext http, ReelGuoDbContext db, ICountryResolver geo, TimeProvider time, CancellationToken ct)
    {
        var detail = (request.Detail ?? "").Trim();
        if (detail.Length > 1000) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_detail", "说明不能超过 1000 个字"));
        var exists = request.TargetType switch
        {
            ReportTarget.Series => await db.Series.AnyAsync(s => s.Id == request.TargetId, ct),
            ReportTarget.Post => await db.Posts.AnyAsync(p => p.Id == request.TargetId, ct),
            ReportTarget.Comment => await db.Comments.AnyAsync(c => c.Id == request.TargetId, ct),
            ReportTarget.User => await db.Users.AnyAsync(u => u.Id == request.TargetId && u.Role != UserRole.Admin, ct),
            _ => false,
        };
        if (!exists) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "举报的内容不存在"));

        var report = new Report
        {
            TargetType = request.TargetType, TargetId = request.TargetId, Reason = request.Reason,
            // Anything involving minors or a real person without consent goes to the top of the queue.
            Urgent = request.Reason is ReportReason.Minor or ReportReason.NonConsensual,
            Detail = detail, ReporterId = await UserAuth.OptionalUserIdAsync(http), Country = geo.Resolve(http),
            Status = ReportStatus.Open, CreatedAt = time.GetUtcNow(),
        };
        db.Reports.Add(report);
        await db.SaveChangesAsync(ct);
        ReelGuoTelemetry.Reports.Add(1, new KeyValuePair<string, object?>("urgent", report.Urgent));
        return TypedResults.Ok(new ReportCreatedDto(report.Id, report.Urgent));
    }
}
