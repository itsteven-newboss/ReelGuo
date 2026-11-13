using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ReelGuo.Api.Users;
using ReelGuo.Data;
using ReelGuo.Data.Entities;
using static ReelGuo.Api.Tests.HttpTestExtensions;

namespace ReelGuo.Api.Tests;

/// <summary>Phase 3: viewer accounts, likes/favourites/follows, comments with review, reports and member bans.</summary>
public sealed class SocialTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    private const string Password = "viewer-pass-1";

    private sealed record Viewer(long Id, string Username, string Token, string? Cookie);

    private static string NewName() => $"u{Guid.NewGuid():N}"[..14];

    /// <summary>Cookies are handled by hand so tests can replay an old refresh token.</summary>
    private static HttpClient Client(ApiFactory api) => api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private static string? RefreshCookieFrom(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Where(v => v.StartsWith(UserAuth.RefreshCookie + "=", StringComparison.Ordinal))
                .Select(v => v[(UserAuth.RefreshCookie.Length + 1)..].Split(';')[0]).FirstOrDefault()
            : null;

    private static async Task<(HttpResponseMessage Response, string? Cookie)> AuthPost(HttpClient client, string path, object? body, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/auth/{path}");
        request.Headers.Add(UserAuth.AppHeader, "1");
        if (body is not null) request.Content = JsonContent.Create(body);
        if (cookie is not null) request.Headers.Add("Cookie", $"{UserAuth.RefreshCookie}={cookie}");
        var response = await client.SendAsync(request, Ct);
        return (response, RefreshCookieFrom(response));
    }

    private static async Task<Viewer> RegisterAsync(HttpClient client, string? username = null)
    {
        var name = username ?? NewName();
        var (response, cookie) = await AuthPost(client, "register", new { username = name, password = Password, nickname = "观众" + name[^4..], accept_terms = true });
        var body = await Task.FromResult(response).OkJsonAsync();
        return new Viewer(body.GetProperty("user").GetProperty("id").GetInt64(), name, body.GetProperty("access_token").GetString()!, cookie);
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string url, string? token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url).From("US");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return client.SendAsync(request, Ct);
    }

    /// <summary>A published series with one ready episode (no media needed for social features).</summary>
    private static async Task<long> SeedSeriesAsync(ApiFactory api, string title)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ReelGuoDbContext>();
        var now = DateTimeOffset.UtcNow;
        var series = new Series
        {
            Title = title, Tags = ["测试"], CategoryId = 1, Status = SeriesStatus.Published, PublishedAt = now.AddDays(-1),
            EpisodeCount = 1, TotalEpisodes = 1, Territories = ["*"], LicenceStart = DateOnly.FromDateTime(now.UtcDateTime.AddDays(-30)),
            RightsHolder = "T", ContractRef = "C", AiGenerated = true, HasRealPerson = false, CreatedAt = now, UpdatedAt = now,
            Episodes = [new Episode { EpisodeNumber = 1, Title = "第1集", DurationSeconds = 60, Status = EpisodeStatus.Ready, CreatedAt = now, UpdatedAt = now }],
        };
        ctx.Series.Add(series);
        await ctx.SaveChangesAsync(Ct);
        return series.Id;
    }

    private static async Task<long> SeedPostAsync(ApiFactory api, string title)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ReelGuoDbContext>();
        var now = DateTimeOffset.UtcNow;
        var post = new Post { Title = title, Status = PostStatus.Published, PublishedAt = now, CreatedAt = now, UpdatedAt = now };
        ctx.Posts.Add(post);
        await ctx.SaveChangesAsync(Ct);
        return post.Id;
    }

    private static List<long> Ids(JsonElement list, string property = "id") =>
        list.EnumerateArray().Select(i => i.GetProperty(property).GetInt64()).ToList();

    // ---------- accounts ----------

    [Fact]
    public async Task Sign_up_sign_in_and_refresh_rotation_with_reuse_detection()
    {
        await using var api = db.CreateApi();
        var client = Client(api);
        var name = NewName();

        var noHeader = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/register")
        { Content = JsonContent.Create(new { username = name, password = Password, accept_terms = true }) }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, noHeader.StatusCode);
        Assert.Equal("app_header_missing", await noHeader.ErrorCodeAsync());

        var (noTerms, _) = await AuthPost(client, "register", new { username = name, password = Password, accept_terms = false });
        Assert.Equal("terms_required", await noTerms.ErrorCodeAsync());

        var viewer = await RegisterAsync(client, name);
        Assert.False(string.IsNullOrEmpty(viewer.Cookie));
        var (duplicate, _) = await AuthPost(client, "register", new { username = name.ToUpperInvariant(), password = Password, accept_terms = true });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var me = await Send(client, HttpMethod.Get, "/api/v1/me", viewer.Token).OkJsonAsync();
        Assert.Equal(name, me.GetProperty("user").GetProperty("username").GetString());
        Assert.Equal(0, me.GetProperty("counts").GetProperty("likes").GetInt32());
        var anonymous = await Send(client, HttpMethod.Get, "/api/v1/me", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("unauthorized", await anonymous.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, HttpMethod.Get, "/api/v1/me", "not-a-token")).StatusCode);

        // Each refresh rotates the cookie and returns a working access token.
        var (rotated, second) = await AuthPost(client, "refresh", null, viewer.Cookie);
        var token = (await Task.FromResult(rotated).OkJsonAsync()).GetProperty("access_token").GetString();
        Assert.NotEqual(viewer.Cookie, second);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Get, "/api/v1/me", token)).StatusCode);

        // Replaying the old cookie means it leaked: the whole sign-in is revoked, including the newer cookie.
        var (replay, cleared) = await AuthPost(client, "refresh", null, viewer.Cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("session_expired", await replay.ErrorCodeAsync());
        Assert.Equal("", cleared);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AuthPost(client, "refresh", null, second)).Response.StatusCode);

        var (wrong, _) = await AuthPost(client, "login", new { username = name, password = "wrong-password" });
        Assert.Equal("invalid_credentials", await wrong.ErrorCodeAsync());
        var (adminAsViewer, _) = await AuthPost(client, "login", new { username = TestKeys.AdminUser, password = TestKeys.AdminPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, adminAsViewer.StatusCode);

        var (login, third) = await AuthPost(client, "login", new { username = name.ToUpperInvariant(), password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var (logout, _) = await AuthPost(client, "logout", null, third);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AuthPost(client, "refresh", null, third)).Response.StatusCode);
    }

    [Fact]
    public async Task Disabling_a_member_ends_access_and_sessions_at_once()
    {
        await using var api = db.CreateApi();
        var client = Client(api);
        var admin = await AdminClient.SignInAsync(api);
        var viewer = await RegisterAsync(client);

        var members = await admin.GetAsync($"/api/admin/members?q={viewer.Username}", Ct).OkJsonAsync();
        var row = Assert.Single(members.GetProperty("items").EnumerateArray());
        Assert.Equal(viewer.Id, row.GetProperty("id").GetInt64());
        Assert.Equal("active", row.GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.AdminSendAsync(HttpMethod.Post, $"/api/admin/members/{viewer.Id}/status", new { active = false, note = "测试封禁" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, HttpMethod.Get, "/api/v1/me", viewer.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AuthPost(client, "refresh", null, viewer.Cookie)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AuthPost(client, "login", new { username = viewer.Username, password = Password })).Response.StatusCode);

        await admin.AdminSendAsync(HttpMethod.Post, $"/api/admin/members/{viewer.Id}/status", new { active = true });
        Assert.Equal(HttpStatusCode.OK, (await AuthPost(client, "login", new { username = viewer.Username, password = Password })).Response.StatusCode);

        var audit = await admin.GetAsync("/api/admin/audit-log?target_type=user", Ct).OkJsonAsync();
        var actions = audit.EnumerateArray().Select(a => a.GetProperty("action").GetString()).ToList();
        Assert.Contains("member.disable", actions);
        Assert.Contains("member.enable", actions);
    }

    // ---------- likes, favourites, follows, history ----------

    [Fact]
    public async Task Likes_favourites_and_follows_are_idempotent_and_keep_counts()
    {
        await using var api = db.CreateApi();
        var client = Client(api);
        var series = await SeedSeriesAsync(api, "互动测试剧");
        var post = await SeedPostAsync(api, "互动测试瓜");
        var a = await RegisterAsync(client);
        var b = await RegisterAsync(client);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, HttpMethod.Put, $"/api/v1/series/{series}/like", null)).StatusCode);

        async Task<JsonElement> Toggle(HttpMethod method, string url, Viewer v) => await Send(client, method, url, v.Token).OkJsonAsync();

        for (var i = 0; i < 2; i++)
        {
            var liked = await Toggle(HttpMethod.Put, $"/api/v1/series/{series}/like", a);
            Assert.True(liked.GetProperty("on").GetBoolean());
            Assert.Equal(1, liked.GetProperty("count").GetInt64());
        }
        Assert.Equal(2, (await Toggle(HttpMethod.Put, $"/api/v1/series/{series}/like", b)).GetProperty("count").GetInt64());
        Assert.Equal(1, (await Toggle(HttpMethod.Delete, $"/api/v1/series/{series}/like", a)).GetProperty("count").GetInt64());
        Assert.Equal(1, (await Toggle(HttpMethod.Delete, $"/api/v1/series/{series}/like", a)).GetProperty("count").GetInt64());
        Assert.Equal(1, (await Toggle(HttpMethod.Put, $"/api/v1/series/{series}/collect", a)).GetProperty("count").GetInt64());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Put, "/api/v1/series/999999999/like", a.Token)).StatusCode);

        Assert.Equal(1, (await Toggle(HttpMethod.Put, $"/api/v1/posts/{post}/like", a)).GetProperty("count").GetInt64());
        Assert.Equal(1, (await Toggle(HttpMethod.Put, $"/api/v1/posts/{post}/collect", a)).GetProperty("count").GetInt64());

        Assert.Equal(1, (await Toggle(HttpMethod.Put, $"/api/v1/users/{b.Id}/follow", a)).GetProperty("count").GetInt64());
        Assert.Equal(1, (await Toggle(HttpMethod.Put, $"/api/v1/users/{b.Id}/follow", a)).GetProperty("count").GetInt64());
        var self = await Send(client, HttpMethod.Put, $"/api/v1/users/{a.Id}/follow", a.Token);
        Assert.Equal("cannot_follow_self", await self.ErrorCodeAsync());

        var me = await Send(client, HttpMethod.Get, "/api/v1/me", a.Token).OkJsonAsync();
        var interactions = me.GetProperty("interactions");
        Assert.Empty(interactions.GetProperty("like_video_ids").EnumerateArray());
        Assert.Equal([series], interactions.GetProperty("favorite_video_ids").EnumerateArray().Select(x => x.GetInt64()));
        Assert.Equal([b.Id], interactions.GetProperty("follow_user_ids").EnumerateArray().Select(x => x.GetInt64()));
        Assert.Equal([post], interactions.GetProperty("like_post_ids").EnumerateArray().Select(x => x.GetInt64()));
        Assert.Equal(1, me.GetProperty("counts").GetProperty("following").GetInt32());

        var favorites = await Send(client, HttpMethod.Get, "/api/v1/me/favorites", a.Token).OkJsonAsync();
        Assert.Equal([series], Ids(favorites.GetProperty("items")));

        var postDetail = await Send(client, HttpMethod.Get, $"/api/v1/posts/{post}", null).OkJsonAsync();
        Assert.Equal(1, postDetail.GetProperty("like_count").GetInt64());
        Assert.Equal(1, postDetail.GetProperty("collect_count").GetInt64());

        var detail = await Send(client, HttpMethod.Get, $"/api/v1/series/{series}", null).OkJsonAsync();
        Assert.Equal(1, detail.GetProperty("counts").GetProperty("like").GetInt64());
        Assert.Equal(1, detail.GetProperty("counts").GetProperty("collect").GetInt64());
    }

    [Fact]
    public async Task First_sign_in_merges_device_state_and_newer_history_wins()
    {
        await using var api = db.CreateApi();
        var client = Client(api);
        var s1 = await SeedSeriesAsync(api, "合并测试一");
        var s2 = await SeedSeriesAsync(api, "合并测试二");
        var viewer = await RegisterAsync(client);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var body = new
        {
            likes = new[] { s1, s2, 999_999_999L },
            favs = new[] { s1 },
            history = new[] { new { id = s1, ep = 1, t = 30.5, dur = 60.0, at = now - 60_000 } },
        };
        var merged = await Send(client, HttpMethod.Post, "/api/v1/me/merge", viewer.Token, body).OkJsonAsync();
        Assert.Equal(new[] { s1, s2 }.Order(), merged.GetProperty("interactions").GetProperty("like_video_ids").EnumerateArray().Select(x => x.GetInt64()).Order());
        Assert.Equal(1, merged.GetProperty("counts").GetProperty("history").GetInt32());
        await Send(client, HttpMethod.Post, "/api/v1/me/merge", viewer.Token, body).OkJsonAsync();   // idempotent
        Assert.Equal(1, (await Send(client, HttpMethod.Get, $"/api/v1/series/{s1}", null).OkJsonAsync()).GetProperty("counts").GetProperty("like").GetInt64());

        async Task<JsonElement> History() => (await Send(client, HttpMethod.Get, "/api/v1/me/history", viewer.Token).OkJsonAsync())[0];

        var older = new { items = new[] { new { id = s1, ep = 1, t = 5.0, dur = 60.0, at = now - 120_000 } } };
        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Put, "/api/v1/me/history", viewer.Token, older)).StatusCode);
        Assert.Equal(30.5, (await History()).GetProperty("t").GetDouble());

        var newer = new { items = new[] { new { id = s1, ep = 1, t = 42.0, dur = 60.0, at = now } } };
        await Send(client, HttpMethod.Put, "/api/v1/me/history", viewer.Token, newer);
        Assert.Equal(42.0, (await History()).GetProperty("t").GetDouble());

        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Delete, "/api/v1/me/history", viewer.Token)).StatusCode);
        Assert.Empty((await Send(client, HttpMethod.Get, "/api/v1/me/history", viewer.Token).OkJsonAsync()).EnumerateArray());
    }

    // ---------- comments ----------

    [Fact]
    public async Task Comments_wait_for_review_and_only_their_author_sees_them_until_approved()
    {
        await using var api = db.CreateApi();
        var client = Client(api);
        var admin = await AdminClient.SignInAsync(api);
        var series = await SeedSeriesAsync(api, "评论测试剧");
        var a = await RegisterAsync(client);
        var b = await RegisterAsync(client);
        var url = $"/api/v1/series/{series}/episodes/1/comments";

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, HttpMethod.Post, url, null, new { text = "未登录" })).StatusCode);
        Assert.Equal("invalid_text", await (await Send(client, HttpMethod.Post, url, a.Token, new { text = "   " })).ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.NotFound,
            (await Send(client, HttpMethod.Post, $"/api/v1/series/{series}/episodes/9/comments", a.Token, new { text = "不存在的集" })).StatusCode);

        var posted = await Send(client, HttpMethod.Post, url, a.Token, new { text = "第一条评论" }).OkJsonAsync();
        Assert.Equal("pending", posted.GetProperty("status").GetString());
        Assert.Equal("US", posted.GetProperty("ip_region").GetString());
        var id = posted.GetProperty("id").GetInt64();

        async Task<List<JsonElement>> Items(string? token, string path) =>
            (await Send(client, HttpMethod.Get, path, token).OkJsonAsync()).GetProperty("items").EnumerateArray().ToList();

        Assert.Empty(await Items(null, url));
        Assert.Empty(await Items(b.Token, url));
        Assert.Equal([id], (await Items(a.Token, url)).Select(c => c.GetProperty("id").GetInt64()));

        var queue = await admin.GetAsync("/api/admin/comments?status=pending", Ct).OkJsonAsync();
        var queued = Assert.Single(queue.GetProperty("items").EnumerateArray(), c => c.GetProperty("id").GetInt64() == id);
        Assert.Equal("评论测试剧", queued.GetProperty("target_title").GetString());
        Assert.True((await admin.GetAsync("/api/admin/moderation/summary", Ct).OkJsonAsync()).GetProperty("pending_comments").GetInt32() >= 1);

        async Task Approve(long commentId) =>
            Assert.Equal(1, (await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/comments/moderate", new { ids = new[] { commentId }, decision = "approve" })
                .OkJsonAsync()).GetProperty("updated").GetInt32());

        await Approve(id);
        Assert.Equal("approved", (await Items(null, url)).Single().GetProperty("status").GetString());

        // Replies are one level deep and also reviewed.
        var reply = await Send(client, HttpMethod.Post, url, b.Token, new { text = "回复一下", parent_id = id }).OkJsonAsync();
        var replyId = reply.GetProperty("id").GetInt64();
        Assert.Equal("invalid_parent", await (await Send(client, HttpMethod.Post, url, a.Token, new { text = "回复的回复", parent_id = replyId })).ErrorCodeAsync());
        await Approve(replyId);

        var top = (await Items(null, url)).Single();
        Assert.Equal(1, top.GetProperty("reply_count").GetInt32());
        Assert.Equal([replyId], Ids(top.GetProperty("replies")));
        Assert.Equal([replyId], (await Items(null, $"/api/v1/comments/{id}/replies")).Select(r => r.GetProperty("id").GetInt64()));
        Assert.Equal(2, (await Send(client, HttpMethod.Get, $"/api/v1/series/{series}", null).OkJsonAsync()).GetProperty("counts").GetProperty("comment").GetInt64());

        var like = await Send(client, HttpMethod.Put, $"/api/v1/comments/{id}/like", b.Token).OkJsonAsync();
        Assert.Equal(1, like.GetProperty("count").GetInt64());
        Assert.True((await Items(b.Token, url)).Single().GetProperty("liked").GetBoolean());
        Assert.False((await Items(null, url)).Single().GetProperty("liked").GetBoolean());

        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Delete, $"/api/v1/comments/{id}", b.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Delete, $"/api/v1/comments/{id}", a.Token)).StatusCode);
        Assert.Empty(await Items(null, url));
        Assert.Equal(0, (await Send(client, HttpMethod.Get, $"/api/v1/series/{series}", null).OkJsonAsync()).GetProperty("counts").GetProperty("comment").GetInt64());
    }

    [Fact]
    public async Task Sensitive_terms_reject_silently_and_posting_is_rate_limited()
    {
        await using var api = db.CreateApi(new Dictionary<string, string?> { ["Moderation:MaxCommentsPer10Minutes"] = "3" });
        var client = Client(api);
        var admin = await AdminClient.SignInAsync(api);
        var post = await SeedPostAsync(api, "敏感词测试瓜");
        var author = await RegisterAsync(client);
        var other = await RegisterAsync(client);
        var url = $"/api/v1/posts/{post}/comments";

        var terms = await admin.AdminSendAsync(HttpMethod.Put, "/api/admin/moderation/terms", new { terms = new[] { "违禁词", " 违禁词 ", "SPAM链接", "" } }).OkJsonAsync();
        Assert.Equal(["SPAM链接", "违禁词"], terms.GetProperty("terms").EnumerateArray().Select(t => t.GetString()));

        var filtered = await Send(client, HttpMethod.Post, url, author.Token, new { text = "快来看 spam链接 哦" }).OkJsonAsync();
        var filteredId = filtered.GetProperty("id").GetInt64();
        Assert.Equal("pending", filtered.GetProperty("status").GetString());

        // The author still sees it "under review"; nobody else does, and it never reaches the review queue.
        var mine = await Send(client, HttpMethod.Get, url, author.Token).OkJsonAsync();
        var shown = Assert.Single(mine.GetProperty("items").EnumerateArray());
        Assert.Equal("pending", shown.GetProperty("status").GetString());
        Assert.Empty((await Send(client, HttpMethod.Get, url, other.Token).OkJsonAsync()).GetProperty("items").EnumerateArray());
        var pending = await admin.GetAsync("/api/admin/comments?status=pending", Ct).OkJsonAsync();
        Assert.DoesNotContain(filteredId, Ids(pending.GetProperty("items")));
        var rejected = await admin.GetAsync("/api/admin/comments?status=rejected", Ct).OkJsonAsync();
        var row = Assert.Single(rejected.GetProperty("items").EnumerateArray(), c => c.GetProperty("id").GetInt64() == filteredId);
        Assert.Contains("SPAM链接", row.GetProperty("reject_reason").GetString());

        await Send(client, HttpMethod.Post, url, author.Token, new { text = "第二条" }).OkJsonAsync();
        await Send(client, HttpMethod.Post, url, author.Token, new { text = "第三条" }).OkJsonAsync();
        var limited = await Send(client, HttpMethod.Post, url, author.Token, new { text = "第四条" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("comment_rate_limited", await limited.ErrorCodeAsync());
    }

    // ---------- reports ----------

    [Fact]
    public async Task Reports_list_urgent_first_and_a_takedown_closes_every_report_on_the_target()
    {
        await using var api = db.CreateApi();
        var client = Client(api);
        var admin = await AdminClient.SignInAsync(api);
        var series = await SeedSeriesAsync(api, "举报测试剧");
        var reporter = await RegisterAsync(client);
        var commenter = await RegisterAsync(client);

        var comment = (await Send(client, HttpMethod.Post, $"/api/v1/series/{series}/episodes/1/comments", commenter.Token, new { text = "被举报的评论" })
            .OkJsonAsync()).GetProperty("id").GetInt64();
        await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/comments/moderate", new { ids = new[] { comment }, decision = "approve" }).OkJsonAsync();

        async Task<JsonElement> Report(string? token, object body) => await Send(client, HttpMethod.Post, "/api/v1/reports", token, body).OkJsonAsync();

        var spam = await Report(null, new { target_type = "series", target_id = series, reason = "spam" });
        Assert.False(spam.GetProperty("urgent").GetBoolean());
        var urgent = await Report(reporter.Token, new { target_type = "series", target_id = series, reason = "non_consensual", detail = "未经本人同意" });
        Assert.True(urgent.GetProperty("urgent").GetBoolean());
        var onComment = await Report(reporter.Token, new { target_type = "comment", target_id = comment, reason = "other" });
        var onUser = await Report(reporter.Token, new { target_type = "user", target_id = commenter.Id, reason = "spam" });
        Assert.Equal(HttpStatusCode.NotFound,
            (await Send(client, HttpMethod.Post, "/api/v1/reports", null, new { target_type = "series", target_id = 999_999_999, reason = "spam" })).StatusCode);

        var list = (await admin.GetAsync("/api/admin/reports", Ct).OkJsonAsync()).GetProperty("items").EnumerateArray().ToList();
        Assert.True(list[0].GetProperty("urgent").GetBoolean());
        Assert.Equal(urgent.GetProperty("id").GetInt64(), list[0].GetProperty("id").GetInt64());
        Assert.Equal(reporter.Username, list[0].GetProperty("reporter_name").GetString());
        Assert.Equal(2, list[0].GetProperty("open_on_target").GetInt32());
        Assert.Contains("举报测试剧", list[0].GetProperty("target_summary").GetString());
        var summary = await admin.GetAsync("/api/admin/moderation/summary", Ct).OkJsonAsync();
        Assert.Equal(4, summary.GetProperty("open_reports").GetInt32());
        Assert.Equal(1, summary.GetProperty("urgent_reports").GetInt32());

        Task<HttpResponseMessage> Resolve(JsonElement report, string action) =>
            admin.AdminSendAsync(HttpMethod.Post, $"/api/admin/reports/{report.GetProperty("id").GetInt64()}/resolve", new { action, note = "测试处理" });

        // Dismissing closes just that report.
        Assert.Equal(1, (await Resolve(onUser, "dismiss").OkJsonAsync()).GetProperty("closed").GetInt32());
        var member = (await admin.GetAsync($"/api/admin/members?q={commenter.Username}", Ct).OkJsonAsync()).GetProperty("items")[0];
        Assert.Equal("active", member.GetProperty("status").GetString());

        // Taking the series down hides it and closes both reports on it.
        Assert.Equal(2, (await Resolve(spam, "takedown").OkJsonAsync()).GetProperty("closed").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Get, $"/api/v1/series/{series}", null)).StatusCode);
        Assert.Equal("already_resolved", await (await Resolve(urgent, "takedown")).ErrorCodeAsync());

        Assert.Equal(1, (await Resolve(onComment, "takedown").OkJsonAsync()).GetProperty("closed").GetInt32());
        var rejected = await admin.GetAsync("/api/admin/comments?status=rejected", Ct).OkJsonAsync();
        var row = Assert.Single(rejected.GetProperty("items").EnumerateArray(), c => c.GetProperty("id").GetInt64() == comment);
        Assert.Equal("举报下架", row.GetProperty("reject_reason").GetString());

        summary = await admin.GetAsync("/api/admin/moderation/summary", Ct).OkJsonAsync();
        Assert.Equal(0, summary.GetProperty("open_reports").GetInt32());
        var audit = await admin.GetAsync("/api/admin/audit-log?target_type=series", Ct).OkJsonAsync();
        Assert.Contains("report.takedown", audit.EnumerateArray().Select(a => a.GetProperty("action").GetString()));
    }

    [Fact]
    public async Task A_user_takedown_disables_the_account()
    {
        await using var api = db.CreateApi();
        var client = Client(api);
        var admin = await AdminClient.SignInAsync(api);
        var target = await RegisterAsync(client);

        var report = await Send(client, HttpMethod.Post, "/api/v1/reports", null, new { target_type = "user", target_id = target.Id, reason = "illegal" }).OkJsonAsync();
        await admin.AdminSendAsync(HttpMethod.Post, $"/api/admin/reports/{report.GetProperty("id").GetInt64()}/resolve", new { action = "takedown" }).OkJsonAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, HttpMethod.Get, "/api/v1/me", target.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AuthPost(client, "refresh", null, target.Cookie)).Response.StatusCode);
    }
}
