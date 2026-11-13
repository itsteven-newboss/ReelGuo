using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ReelGuo.Api.Users;
using ReelGuo.Data;
using ReelGuo.Data.Entities;
using static ReelGuo.Api.Tests.HttpTestExtensions;

namespace ReelGuo.Api.Tests;

/// <summary>The portal layout's data: 专题, 公告 and official addresses, viewer ratings, and the listing sorts.</summary>
public sealed class PortalTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    private static async Task<long> SeedAsync(ApiFactory api, string title, string[]? territories = null, bool original = false, int weight = 0, double ageDays = 1)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ReelGuoDbContext>();
        var now = DateTimeOffset.UtcNow;
        var series = new Series
        {
            Title = title, Tags = ["测试"], CategoryId = 1, Status = SeriesStatus.Published, PublishedAt = now.AddDays(-ageDays),
            LastEpisodeAt = now.AddDays(-ageDays), EpisodeCount = 1, TotalEpisodes = 1, Territories = [.. territories ?? ["*"]],
            LicenceStart = DateOnly.FromDateTime(now.UtcDateTime.AddDays(-30)), RightsHolder = "T", ContractRef = "C", AiGenerated = true,
            HasRealPerson = false, IsOriginal = original, RecommendWeight = weight, CreatedAt = now, UpdatedAt = now,
            Episodes = [new Episode { EpisodeNumber = 1, Title = "第1集", DurationSeconds = 60, Status = EpisodeStatus.Ready, CreatedAt = now, UpdatedAt = now }],
        };
        ctx.Series.Add(series);
        await ctx.SaveChangesAsync(Ct);
        return series.Id;
    }

    private static Task<HttpResponseMessage> Get(HttpClient client, string url, string country = "US", string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url).From(country);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task Topics_show_only_series_the_visitor_may_watch_in_their_order()
    {
        await using var api = db.CreateApi();
        var admin = await AdminClient.SignInAsync(api);
        var client = api.CreateClient();
        var a = await SeedAsync(api, "专题甲");
        var jpOnly = await SeedAsync(api, "仅日本", territories: ["JP"]);
        var b = await SeedAsync(api, "专题乙");

        var invalid = await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/topics", new { title = "", description = "", published = true, sort_order = 0, series_ids = new[] { a } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var topic = await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/topics",
            new { title = "精品高分短剧专辑", description = "编辑精选", published = true, sort_order = 1, series_ids = new[] { b, jpOnly, a } }).OkJsonAsync();
        var id = topic.GetProperty("id").GetInt32();
        Assert.Equal([b, jpOnly, a], topic.GetProperty("series").EnumerateArray().Select(s => s.GetProperty("id").GetInt64()));
        await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/topics", new { title = "草稿专题", description = "", published = false, sort_order = 0, series_ids = new[] { a } }).OkJsonAsync();

        var list = await Get(client, "/api/v1/topics").OkJsonAsync();
        var card = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(2, card.GetProperty("count").GetInt32());   // the JP-only series isn't counted for a US visitor
        var detail = await Get(client, $"/api/v1/topics/{id}").OkJsonAsync();
        Assert.Equal([b, a], detail.GetProperty("items").EnumerateArray().Select(s => s.GetProperty("id").GetInt64()));
        Assert.Equal(3, (await Get(client, $"/api/v1/topics/{id}", "JP").OkJsonAsync()).GetProperty("items").GetArrayLength());

        // Reorder and trim.
        await admin.AdminSendAsync(HttpMethod.Put, $"/api/admin/topics/{id}",
            new { title = "精品高分短剧专辑", description = "编辑精选", published = true, sort_order = 1, series_ids = new[] { a } }).OkJsonAsync();
        Assert.Equal([a], (await Get(client, $"/api/v1/topics/{id}").OkJsonAsync()).GetProperty("items").EnumerateArray().Select(s => s.GetProperty("id").GetInt64()));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.AdminSendAsync(HttpMethod.Delete, $"/api/admin/topics/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(client, $"/api/v1/topics/{id}")).StatusCode);
    }

    [Fact]
    public async Task The_site_endpoint_carries_active_announcements_and_the_official_addresses()
    {
        await using var api = db.CreateApi(new Dictionary<string, string?>
        {
            ["App:Mirrors:0"] = "https://app.example.com", ["App:Mirrors:1"] = "", ["Site:Telegram"] = "https://t.me/reelguo", ["Site:PermanentDomain"] = "reelguo.example.org",
        });
        var admin = await AdminClient.SignInAsync(api);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/announcements", new { text = "坏链接", url = "javascript:alert(1)", active = true, sort_order = 0 })).StatusCode);
        await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/announcements", new { text = "最新地址：app.example.com", url = "/go-home", active = true, sort_order = 1 }).OkJsonAsync();
        await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/announcements", new { text = "已隐藏的公告", url = (string?)null, active = false, sort_order = 0 }).OkJsonAsync();

        var site = await Get(api.CreateClient(), "/api/v1/site").OkJsonAsync();
        Assert.Equal("ReelGuo", site.GetProperty("name").GetString());
        var notice = Assert.Single(site.GetProperty("announcements").EnumerateArray());
        Assert.Equal("/go-home", notice.GetProperty("url").GetString());
        Assert.Equal("https://app.example.com", Assert.Single(site.GetProperty("mirrors").EnumerateArray()).GetString());
        Assert.Equal("https://t.me/reelguo", site.GetProperty("telegram").GetString());
        Assert.Equal(JsonValueKind.Null, site.GetProperty("email").ValueKind);
    }

    [Fact]
    public async Task Viewers_rate_series_once_each_and_cards_carry_their_channel()
    {
        await using var api = db.CreateApi();
        var client = api.CreateClient();
        var id = await SeedAsync(api, "评分测试");

        async Task<string> Register()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/register")
            {
                Content = JsonContent.Create(new { username = $"r{Guid.NewGuid():N}"[..14], password = "viewer-pass-1", accept_terms = true }),
            };
            request.Headers.Add(UserAuth.AppHeader, "1");
            return (await client.SendAsync(request, Ct).OkJsonAsync()).GetProperty("access_token").GetString()!;
        }
        Task<HttpResponseMessage> Rate(string token, int score)
        {
            var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/series/{id}/rating") { Content = JsonContent.Create(new { score }) }.From("US");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client.SendAsync(request, Ct);
        }

        var detail = await Get(client, $"/api/v1/series/{id}").OkJsonAsync();
        Assert.Equal(0, detail.GetProperty("rating").GetProperty("count").GetInt32());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("rating").GetProperty("average").ValueKind);

        var one = await Register();
        var two = await Register();
        Assert.Equal(HttpStatusCode.BadRequest, (await Rate(one, 11)).StatusCode);
        await Rate(one, 6).OkJsonAsync();
        await Rate(one, 8).OkJsonAsync();                          // replaces the 6
        var after = await Rate(two, 9).OkJsonAsync();
        Assert.Equal(9, after.GetProperty("mine").GetInt32());
        Assert.Equal(8.5, after.GetProperty("rating").GetProperty("average").GetDouble());
        Assert.Equal(2, after.GetProperty("rating").GetProperty("count").GetInt32());
        Assert.Equal(8, (await Get(client, $"/api/v1/series/{id}/rating", token: one).OkJsonAsync()).GetProperty("mine").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(client, $"/api/v1/series/{id}/rating")).StatusCode);

        var feed = await Get(client, "/api/v1/feed?size=30").OkJsonAsync();
        var card = feed.GetProperty("items").EnumerateArray().Single(c => c.GetProperty("id").GetInt64() == id);
        Assert.Equal("ai-duanju", card.GetProperty("category_slug").GetString());
        Assert.Equal("AI成人短剧", card.GetProperty("category_name").GetString());
    }

    [Fact]
    public async Task Listing_sorts_cover_the_portal_sections()
    {
        await using var api = db.CreateApi();
        var client = api.CreateClient();
        var old = await SeedAsync(api, "旧剧", ageDays: 20);
        var fresh = await SeedAsync(api, "新剧", ageDays: 0.1);
        var picked = await SeedAsync(api, "编辑精选", weight: 50, ageDays: 5);
        var original = await SeedAsync(api, "独家原创", original: true, ageDays: 3);

        List<long> Ids(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("id").GetInt64()).ToList();
        var updated = Ids(await Get(client, "/api/v1/feed?sort=updated&size=30").OkJsonAsync());
        Assert.True(updated.IndexOf(fresh) < updated.IndexOf(old));
        Assert.Equal(picked, Ids(await Get(client, "/api/v1/feed?sort=picks&size=4").OkJsonAsync())[0]);
        var originals = Ids(await Get(client, "/api/v1/categories/ai-duanju/series?sort=original&size=30").OkJsonAsync());
        Assert.Contains(original, originals);
        Assert.DoesNotContain(fresh, originals);
        var random = Ids(await Get(client, "/api/v1/categories/ai-duanju/series?sort=random&size=30").OkJsonAsync());
        Assert.Equal(updated.Order(), random.Order());
    }
}
