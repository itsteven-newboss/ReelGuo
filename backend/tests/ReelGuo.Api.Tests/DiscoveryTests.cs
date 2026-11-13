using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ReelGuo.Data;
using ReelGuo.Data.Catalog;
using ReelGuo.Data.Content;
using ReelGuo.Data.Entities;
using static ReelGuo.Api.Tests.HttpTestExtensions;

namespace ReelGuo.Api.Tests;

public sealed class PostRendererTests
{
    private static readonly PostRenderer Renderer = new("/media");

    [Fact]
    public void Raw_html_and_scripts_never_survive()
    {
        var html = Renderer.Render("Hello <script>alert(1)</script> <b onclick=x>bold</b>\n\n<img src=x onerror=alert(1)>");
        // Raw HTML is shown as literal text, never as markup.
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("<b ", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void Markdown_formatting_links_and_own_images_are_kept()
    {
        var html = Renderer.Render("## 标题\n\n**粗体** [链接](https://example.com)\n\n![图](/media/p/posts/1-img-ab.jpg)");
        Assert.Contains("<h2>标题</h2>", html);
        Assert.Contains("<strong>粗体</strong>", html);
        Assert.Contains("rel=\"noopener noreferrer nofollow\"", html);
        Assert.Contains("target=\"_blank\"", html);
        Assert.Contains("src=\"/media/p/posts/1-img-ab.jpg\"", html);
    }

    [Theory]
    [InlineData("![x](https://tracker.example/pixel.gif)")]
    [InlineData("![x](/media/m/1/sig/s/1/e/1/v1/master.m3u8)")]
    public void Foreign_images_are_dropped(string markdown) => Assert.DoesNotContain("<img", Renderer.Render(markdown));

    [Fact]
    public void Javascript_links_are_dropped() => Assert.DoesNotContain("javascript:", Renderer.Render("[x](javascript:alert(1))"));
}

public sealed class RankingMathTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);

    private static SeriesSignals S(long plays7 = 0, long completes7 = 0, double ageDays = 1, int weight = 0, long plays3 = 0, long prev3 = 0) =>
        new(1, Now.AddDays(-ageDays), weight, 0, 0, 0, plays7, completes7, plays3, prev3, plays7);

    [Fact]
    public void Hot_rewards_plays_and_completions_and_decays_with_age()
    {
        Assert.True(RankingMath.Hot(S(plays7: 10, completes7: 5), Now) > RankingMath.Hot(S(plays7: 10), Now));
        Assert.True(RankingMath.Hot(S(plays7: 10, ageDays: 1), Now) > RankingMath.Hot(S(plays7: 10, ageDays: 60), Now));
        Assert.Equal(0, RankingMath.Hot(S(), Now));
    }

    [Fact]
    public void Recommend_lists_editor_picks_first_and_skips_unseen_unpicked_series()
    {
        Assert.Null(RankingMath.Recommend(S()));
        Assert.True(RankingMath.Recommend(S(weight: 1))! > RankingMath.Recommend(S(plays7: 100, completes7: 100))!);
    }

    [Fact]
    public void Potential_covers_the_last_14_days_and_favours_growth()
    {
        Assert.Null(RankingMath.Potential(S(ageDays: 15), Now));
        Assert.True(RankingMath.Potential(S(plays3: 10, prev3: 0), Now)! > RankingMath.Potential(S(plays3: 10, prev3: 10), Now)!);
        Assert.True(RankingMath.Potential(S(ageDays: 1), Now)! > RankingMath.Potential(S(ageDays: 10), Now)!);
    }
}

public sealed class DiscoveryTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    /// <summary>Inserts a published, watchable series directly (no encoding needed for discovery tests).</summary>
    private static async Task<long> SeedAsync(ApiFactory api, string title, string[] tags, double ageDays = 1, int weight = 0,
        string[]? territories = null, string description = "")
    {
        await using var scope = api.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ReelGuoDbContext>();
        var now = DateTimeOffset.UtcNow;
        var series = new Series
        {
            Title = title, Description = description, Tags = [.. tags], CategoryId = 1, Status = SeriesStatus.Published,
            PublishedAt = now.AddDays(-ageDays), EpisodeCount = 1, TotalEpisodes = 1, RecommendWeight = weight,
            Territories = [.. territories ?? ["*"]], LicenceStart = DateOnly.FromDateTime(now.UtcDateTime.AddDays(-30)),
            RightsHolder = "T", ContractRef = "C", AiGenerated = true, HasRealPerson = false, CreatedAt = now, UpdatedAt = now,
        };
        ctx.Series.Add(series);
        await ctx.SaveChangesAsync(Ct);
        return series.Id;
    }

    private static Task<HttpResponseMessage> Get(HttpClient client, string url, string country = "US") =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Get, url).From(country), Ct);

    [Fact]
    public async Task Search_matches_titles_tags_and_descriptions_but_respects_licences()
    {
        await using var api = db.CreateApi();
        var client = api.CreateClient();
        var title = await SeedAsync(api, "搜索测试：霸道总裁爱上我", ["都市"]);
        var tagged = await SeedAsync(api, "另一部剧", ["搜索专用标签"]);
        var described = await SeedAsync(api, "第三部剧", ["古风"], description: "一个关于百分百_特殊字符的故事");
        var japanOnly = await SeedAsync(api, "搜索测试：仅日本", ["都市"], territories: ["JP"]);

        async Task<List<long>> Ids(string q, string country = "US") =>
            (await Get(client, $"/api/v1/search?q={Uri.EscapeDataString(q)}", country).OkJsonAsync())
                .GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt64()).ToList();

        Assert.Contains(title, await Ids("霸道总裁"));
        Assert.Contains(tagged, await Ids("搜索专用标签"));
        Assert.Contains(described, await Ids("百分百_特殊"));
        Assert.DoesNotContain(japanOnly, await Ids("搜索测试"));
        Assert.Contains(japanOnly, await Ids("搜索测试", "JP"));
        Assert.Empty(await Ids("%"));   // LIKE wildcards are escaped, not interpreted
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(client, "/api/v1/search?q=%20")).StatusCode);
    }

    [Fact]
    public async Task Play_events_drive_the_rankings()
    {
        await using var api = db.CreateApi();
        var viewer = api.CreateClient();
        var admin = await AdminClient.SignInAsync(api);
        var popular = await SeedAsync(api, "热门剧", ["热血"], ageDays: 2);
        var quiet = await SeedAsync(api, "冷门剧", ["日常"], ageDays: 2);
        var picked = await SeedAsync(api, "编辑推荐剧", ["甜宠"], ageDays: 40, weight: 5);
        var hiddenInUs = await SeedAsync(api, "仅日本热门", ["热血"], ageDays: 2, territories: ["JP"]);

        async Task Send(string session, params (string Type, long Id, int Ep)[] events)
        {
            var body = new { session_id = session, events = events.Select(e => new { type = e.Type, series_id = e.Id, ep = e.Ep }) };
            var res = await viewer.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/v1/events") { Content = JsonContent.Create(body) }.From("US"), Ct);
            Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        }

        for (var i = 0; i < 5; i++) await Send($"session-{i:D4}", ("start", popular, 1), ("complete", popular, 1), ("start", hiddenInUs, 1));
        await Send("session-9999", ("start", quiet, 1), ("start", 999_999, 1), ("bogus", quiet, 1));

        var bad = await viewer.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/v1/events")
        { Content = JsonContent.Create(new { session_id = "x", events = new[] { new { type = "start", series_id = popular, ep = 1 } } }) }.From("US"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var run = await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/ranks/recompute").OkJsonAsync();
        Assert.True(run.GetProperty("hot").GetInt32() >= 2);

        List<JsonElement> Items(JsonElement list) => list.GetProperty("items").EnumerateArray().ToList();
        var hot = Items(await Get(viewer, "/api/v1/ranks/hot").OkJsonAsync());
        var hotIds = hot.Select(i => i.GetProperty("video_id").GetInt64()).ToList();
        Assert.True(hotIds.IndexOf(popular) < hotIds.IndexOf(quiet));
        Assert.DoesNotContain(hiddenInUs, hotIds);
        Assert.Equal(Enumerable.Range(1, hot.Count), hot.Select(i => i.GetProperty("rank").GetInt32()));   // renumbered after filtering
        Assert.Equal("热度", hot[0].GetProperty("metric_label").GetString());
        Assert.Equal(5, hot.Single(i => i.GetProperty("video_id").GetInt64() == popular).GetProperty("metrics").GetProperty("play").GetInt64());

        Assert.Contains(hiddenInUs, Items(await Get(viewer, "/api/v1/ranks/hot", "JP").OkJsonAsync()).Select(i => i.GetProperty("video_id").GetInt64()));
        Assert.Equal(picked, Items(await Get(viewer, "/api/v1/ranks/recommend").OkJsonAsync())[0].GetProperty("video_id").GetInt64());
        var potential = Items(await Get(viewer, "/api/v1/ranks/potential").OkJsonAsync()).Select(i => i.GetProperty("video_id").GetInt64()).ToList();
        Assert.Contains(popular, potential);
        Assert.DoesNotContain(picked, potential);   // published 40 days ago
        Assert.Equal(HttpStatusCode.NotFound, (await Get(viewer, "/api/v1/ranks/nope")).StatusCode);
    }

    [Fact]
    public async Task Admin_series_list_filters_by_status_in_json_spelling()
    {
        await using var api = db.CreateApi();
        var admin = await AdminClient.SignInAsync(api);
        var published = await SeedAsync(api, "状态筛选测试", ["都市"]);

        var list = await admin.GetAsync("/api/admin/series?status=published&size=100", Ct).OkJsonAsync();
        var items = list.GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(published, items.Select(i => i.GetProperty("id").GetInt64()));
        Assert.All(items, i => Assert.Equal("published", i.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task Suggestions_include_popular_tags_and_rotate_recommendations()
    {
        await using var api = db.CreateApi();
        await SeedAsync(api, "推荐甲", ["独特标签甲"]);
        var client = api.CreateClient();
        var s = await Get(client, "/api/v1/search/suggest?seed=1").OkJsonAsync();
        Assert.Contains("独特标签甲", s.GetProperty("hot_tags").EnumerateArray().Select(t => t.GetString()));
        Assert.InRange(s.GetProperty("recommended").GetArrayLength(), 1, 6);
    }

    [Fact]
    public async Task Posts_are_sanitised_need_a_cover_and_show_only_when_published()
    {
        await using var api = db.CreateApi();
        var admin = await AdminClient.SignInAsync(api);
        var viewer = api.CreateClient();
        var linked = await SeedAsync(api, "关联剧", ["都市"]);
        var japanOnly = await SeedAsync(api, "关联剧（仅日本）", ["都市"], territories: ["JP"]);

        var post = await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/posts", new
        {
            title = "吃瓜测试", summary = "摘要", tags = new[] { "爆料" }, series_id = linked,
            content_markdown = "正文 **重点** <script>alert(1)</script> ![](https://evil.example/x.png)",
        }).OkJsonAsync();
        var id = post.GetProperty("id").GetInt64();
        var html = post.GetProperty("content_html").GetString()!;
        Assert.Contains("<strong>重点</strong>", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("evil.example", html);

        Assert.Equal(HttpStatusCode.NotFound, (await Get(viewer, $"/api/v1/posts/{id}")).StatusCode);   // draft

        var blocked = await admin.AdminSendAsync(HttpMethod.Post, $"/api/admin/posts/{id}/publish");
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        Assert.Contains("请上传封面", (await blocked.JsonAsync()).GetProperty("error").GetProperty("details").EnumerateArray().Select(d => d.GetString()));

        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0];
        var cover = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/admin/posts/{id}/cover")
        { Content = new ByteArrayContent(png) }.WithCsrf(), Ct);
        Assert.Equal(HttpStatusCode.OK, cover.StatusCode);
        await admin.AdminSendAsync(HttpMethod.Post, $"/api/admin/posts/{id}/publish").OkJsonAsync();

        var list = await Get(viewer, "/api/v1/posts").OkJsonAsync();
        Assert.Contains(id, list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt64()));
        var detail = await Get(viewer, $"/api/v1/posts/{id}").OkJsonAsync();
        Assert.Equal(linked, detail.GetProperty("series").GetProperty("id").GetInt64());
        Assert.True(detail.GetProperty("hot").GetInt64() >= 1);

        // A linked series the visitor may not watch is left out.
        var update = new { title = "吃瓜测试", summary = "摘要", tags = new[] { "爆料" }, series_id = japanOnly, content_markdown = "正文" };
        await admin.AdminSendAsync(HttpMethod.Put, $"/api/admin/posts/{id}", update).OkJsonAsync();
        Assert.Equal(JsonValueKind.Null, (await Get(viewer, $"/api/v1/posts/{id}").OkJsonAsync()).GetProperty("series").ValueKind);

        await admin.AdminSendAsync(HttpMethod.Post, $"/api/admin/posts/{id}/unpublish");
        Assert.Equal(HttpStatusCode.NotFound, (await Get(viewer, $"/api/v1/posts/{id}")).StatusCode);
    }
}
