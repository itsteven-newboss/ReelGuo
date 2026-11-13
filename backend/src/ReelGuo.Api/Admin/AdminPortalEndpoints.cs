using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Data;
using ReelGuo.Data.Entities;
using ReelGuo.Data.Media;

namespace ReelGuo.Api.Admin;

public sealed record AdminTopicSeriesDto(long Id, string Title, string? CoverUrl, SeriesStatus Status);

public sealed record AdminTopicDto(
    int Id, string Title, string Description, bool Published, int SortOrder, List<AdminTopicSeriesDto> Series, DateTimeOffset UpdatedAt);

/// <summary><c>series_ids</c> in display order; the first visible one supplies the cover.</summary>
public sealed record TopicInput(string Title, string? Description, bool Published, int SortOrder, List<long> SeriesIds);

public sealed record AdminAnnouncementDto(int Id, string Text, string? Url, bool Active, int SortOrder, DateTimeOffset UpdatedAt);

public sealed record AnnouncementInput(string Text, string? Url, bool Active, int SortOrder);

/// <summary>专题 (collections) and 公告 (the announcement bar).</summary>
public static class AdminPortalEndpoints
{
    public static RouteGroupBuilder MapAdminPortal(this RouteGroupBuilder admin)
    {
        var topics = admin.MapGroup("/topics").WithTags("Admin: portal");
        topics.MapGet("/", ListTopics).WithSummary("Every collection, including unpublished ones.");
        topics.MapPost("/", CreateTopic).WithSummary("Creates a collection.");
        topics.MapPut("/{id:int}", UpdateTopic).WithSummary("Edits a collection and its series (in order).");
        topics.MapDelete("/{id:int}", DeleteTopic).WithSummary("Deletes a collection (the series stay).");

        var notices = admin.MapGroup("/announcements").WithTags("Admin: portal");
        notices.MapGet("/", ListAnnouncements).WithSummary("Every announcement.");
        notices.MapPost("/", CreateAnnouncement).WithSummary("Adds an announcement to the 公告 bar.");
        notices.MapPut("/{id:int}", UpdateAnnouncement).WithSummary("Edits, shows or hides an announcement.");
        notices.MapDelete("/{id:int}", DeleteAnnouncement).WithSummary("Deletes an announcement.");
        return admin;
    }

    // ---------- topics ----------

    private static async Task<List<AdminTopicDto>> LoadTopicsAsync(ReelGuoDbContext db, MediaUrlSigner signer, int? id, CancellationToken ct)
    {
        var topics = await db.Topics.AsNoTracking().Where(t => id == null || t.Id == id)
            .Include(t => t.Items.OrderBy(i => i.Position)).ThenInclude(i => i.Series)
            .OrderBy(t => t.SortOrder).ThenByDescending(t => t.Id).ToListAsync(ct);
        return topics.Select(t => new AdminTopicDto(t.Id, t.Title, t.Description, t.Published, t.SortOrder,
            t.Items.OrderBy(i => i.Position).Select(i => new AdminTopicSeriesDto(i.SeriesId, i.Series.Title, signer.PublicUrlOrNull(i.Series.CoverKey), i.Series.Status)).ToList(),
            t.UpdatedAt)).ToList();
    }

    private static async Task<Ok<List<AdminTopicDto>>> ListTopics(ReelGuoDbContext db, MediaUrlSigner signer, CancellationToken ct) =>
        TypedResults.Ok(await LoadTopicsAsync(db, signer, null, ct));

    private static async Task<string?> ValidateAsync(TopicInput input, ReelGuoDbContext db, CancellationToken ct)
    {
        var title = (input.Title ?? "").Trim();
        if (title.Length is 0 or > 40) return "专题名称需为 1-40 个字符";
        if ((input.Description ?? "").Trim().Length > 500) return "简介不超过 500 字";
        var ids = (input.SeriesIds ?? []).Distinct().ToList();
        if (ids.Count > 200) return "一个专题最多 200 部剧";
        var found = await db.Series.CountAsync(s => ids.Contains(s.Id), ct);
        return found != ids.Count ? "部分剧集不存在" : null;
    }

    private static void Apply(Topic topic, TopicInput input, DateTimeOffset now)
    {
        topic.Title = input.Title.Trim();
        topic.Description = (input.Description ?? "").Trim();
        topic.Published = input.Published;
        topic.SortOrder = input.SortOrder;
        topic.UpdatedAt = now;
        topic.Items = (input.SeriesIds ?? []).Distinct().Select((sid, i) => new TopicSeries { TopicId = topic.Id, SeriesId = sid, Position = i }).ToList();
    }

    private static async Task<Results<Ok<AdminTopicDto>, BadRequest<ApiErrorResponse>>> CreateTopic(
        TopicInput input, HttpContext http, ReelGuoDbContext db, MediaUrlSigner signer, TimeProvider time, CancellationToken ct)
    {
        if (await ValidateAsync(input, db, ct) is { } error) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_topic", error));
        var now = time.GetUtcNow();
        var topic = new Topic { Title = "", CreatedAt = now };
        Apply(topic, input, now);
        db.Topics.Add(topic);
        await db.SaveChangesAsync(ct);
        db.AddAudit(now, AdminActor.From(http), "topic.create", "topic", topic.Id.ToString(), new { topic.Title, count = topic.Items.Count, topic.Published });
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok((await LoadTopicsAsync(db, signer, topic.Id, ct)).Single());
    }

    private static async Task<Results<Ok<AdminTopicDto>, NotFound<ApiErrorResponse>, BadRequest<ApiErrorResponse>>> UpdateTopic(
        int id, TopicInput input, HttpContext http, ReelGuoDbContext db, MediaUrlSigner signer, TimeProvider time, CancellationToken ct)
    {
        var topic = await db.Topics.Include(t => t.Items).FirstOrDefaultAsync(t => t.Id == id, ct);
        if (topic is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "专题不存在"));
        if (await ValidateAsync(input, db, ct) is { } error) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_topic", error));
        db.TopicSeries.RemoveRange(topic.Items);
        await db.SaveChangesAsync(ct);
        var now = time.GetUtcNow();
        Apply(topic, input, now);
        db.AddAudit(now, AdminActor.From(http), "topic.update", "topic", id.ToString(), new { topic.Title, count = topic.Items.Count, topic.Published });
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok((await LoadTopicsAsync(db, signer, id, ct)).Single());
    }

    private static async Task<Results<NoContent, NotFound<ApiErrorResponse>>> DeleteTopic(int id, HttpContext http, ReelGuoDbContext db, TimeProvider time, CancellationToken ct)
    {
        var topic = await db.Topics.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (topic is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "专题不存在"));
        db.Topics.Remove(topic);
        db.AddAudit(time, AdminActor.From(http), "topic.delete", "topic", id.ToString(), new { topic.Title });
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    // ---------- announcements ----------

    private static AdminAnnouncementDto ToDto(Announcement a) => new(a.Id, a.Text, a.Url, a.Active, a.SortOrder, a.UpdatedAt);

    private static async Task<Ok<List<AdminAnnouncementDto>>> ListAnnouncements(ReelGuoDbContext db, CancellationToken ct) =>
        TypedResults.Ok((await db.Announcements.AsNoTracking().OrderBy(a => a.SortOrder).ThenByDescending(a => a.Id).ToListAsync(ct)).Select(ToDto).ToList());

    private static string? Validate(AnnouncementInput input)
    {
        if ((input.Text ?? "").Trim().Length is 0 or > 200) return "公告内容需为 1-200 个字符";
        if (!string.IsNullOrWhiteSpace(input.Url)
            && !(input.Url.Trim().StartsWith('/') || Uri.TryCreate(input.Url.Trim(), UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps))
            return "链接需为站内路径（/ 开头）或 https:// 地址";
        return null;
    }

    private static void Apply(Announcement a, AnnouncementInput input, DateTimeOffset now)
    {
        a.Text = input.Text.Trim();
        a.Url = string.IsNullOrWhiteSpace(input.Url) ? null : input.Url.Trim();
        a.Active = input.Active;
        a.SortOrder = input.SortOrder;
        a.UpdatedAt = now;
    }

    private static async Task<Results<Ok<AdminAnnouncementDto>, BadRequest<ApiErrorResponse>>> CreateAnnouncement(
        AnnouncementInput input, HttpContext http, ReelGuoDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (Validate(input) is { } error) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_announcement", error));
        var now = time.GetUtcNow();
        var a = new Announcement { Text = "", CreatedAt = now };
        Apply(a, input, now);
        db.Announcements.Add(a);
        await db.SaveChangesAsync(ct);
        db.AddAudit(now, AdminActor.From(http), "announcement.create", "announcement", a.Id.ToString(), new { a.Text, a.Active });
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(a));
    }

    private static async Task<Results<Ok<AdminAnnouncementDto>, NotFound<ApiErrorResponse>, BadRequest<ApiErrorResponse>>> UpdateAnnouncement(
        int id, AnnouncementInput input, HttpContext http, ReelGuoDbContext db, TimeProvider time, CancellationToken ct)
    {
        var a = await db.Announcements.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "公告不存在"));
        if (Validate(input) is { } error) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_announcement", error));
        var now = time.GetUtcNow();
        Apply(a, input, now);
        db.AddAudit(now, AdminActor.From(http), "announcement.update", "announcement", id.ToString(), new { a.Text, a.Active });
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(a));
    }

    private static async Task<Results<NoContent, NotFound<ApiErrorResponse>>> DeleteAnnouncement(int id, HttpContext http, ReelGuoDbContext db, TimeProvider time, CancellationToken ct)
    {
        var a = await db.Announcements.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "公告不存在"));
        db.Announcements.Remove(a);
        db.AddAudit(time, AdminActor.From(http), "announcement.delete", "announcement", id.ToString(), new { a.Text });
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }
}
