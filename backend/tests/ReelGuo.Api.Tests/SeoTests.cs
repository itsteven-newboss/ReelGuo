using System.Net;
using Microsoft.Extensions.DependencyInjection;
using ReelGuo.Data;
using ReelGuo.Data.Entities;
using static ReelGuo.Api.Tests.HttpTestExtensions;

namespace ReelGuo.Api.Tests;

/// <summary>Search-engine pages: the app's HTML with each page's head tags and content, robots.txt and the sitemaps.</summary>
public sealed class SeoTests(PostgresFixture db) : IClassFixture<PostgresFixture>, IDisposable
{
    private const string Template = """
        <!doctype html>
        <html lang="zh-CN">
          <head>
            <meta charset="UTF-8" />
            <meta name="robots" content="noindex" />
            <title>ReelGuo</title>
            <script src="/boot.js"></script>
          </head>
          <body>
            <div id="root"></div>
            <script type="module" src="/assets/app.js"></script>
          </body>
        </html>
        """;

    private readonly string _templatePath = Path.Combine(Path.GetTempPath(), $"reelguo-seo-{Guid.NewGuid():N}.html");

    public void Dispose()
    {
        if (File.Exists(_templatePath)) File.Delete(_templatePath);
    }

    private ApiFactory Api(bool indexing = false)
    {
        File.WriteAllText(_templatePath, Template);
        return db.CreateApi(new Dictionary<string, string?>
        {
            ["Seo:TemplatePath"] = _templatePath,
            ["Seo:AllowIndexing"] = indexing ? "true" : "false",
            ["Seo:PublicBaseUrl"] = "https://reelguo.example",
            ["Seo:CacheSeconds"] = "5",
        });
    }

    private static async Task<long> SeedAsync(ApiFactory api, string title, string[]? territories = null, int episodes = 3)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ReelGuoDbContext>();
        var now = DateTimeOffset.UtcNow;
        var series = new Series
        {
            Title = title, Description = "身份反转的逆袭故事。", Tags = ["逆袭", "都市"], CategoryId = 1, Status = SeriesStatus.Published,
            PublishedAt = now.AddDays(-1), LastEpisodeAt = now.AddDays(-1), EpisodeCount = episodes, TotalEpisodes = episodes,
            Territories = [.. territories ?? ["*"]], LicenceStart = DateOnly.FromDateTime(now.UtcDateTime.AddDays(-30)), RightsHolder = "T",
            ContractRef = "C", AiGenerated = true, HasRealPerson = false, Score = 8.6, CreatedAt = now, UpdatedAt = now,
            Episodes = [.. Enumerable.Range(1, episodes).Select(n => new Episode
            {
                EpisodeNumber = n, Title = $"第{n}集", DurationSeconds = 60, Status = EpisodeStatus.Ready, CreatedAt = now, UpdatedAt = now,
            })],
        };
        ctx.Series.Add(series);
        await ctx.SaveChangesAsync(Ct);
        return series.Id;
    }

    private static async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> Page(ApiFactory api, string url, string country = "US")
    {
        var response = await api.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Get, url).From(country), Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct), response);
    }

    [Fact]
    public async Task Series_and_episode_pages_carry_their_title_canonical_structured_data_and_content()
    {
        await using var api = Api(indexing: true);
        var id = await SeedAsync(api, "契约总裁的替身新娘");

        var (status, html, response) = await Page(api, $"/video/{id}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.StartsWith("text/html", response.Content.Headers.ContentType!.ToString());
        Assert.Contains("<title>契约总裁的替身新娘全集在线观看 - ", html);
        Assert.Contains($"<link rel=\"canonical\" href=\"https://reelguo.example/video/{id}\" />", html);
        Assert.Contains("<meta name=\"robots\" content=\"index,follow", html);
        Assert.DoesNotContain("content=\"noindex\"", html);                       // the template's own robots tag is replaced
        Assert.Contains("\"@type\":\"TVSeries\"", html);
        Assert.Contains("\"@type\":\"BreadcrumbList\"", html);
        Assert.Contains("<div id=\"seo\" class=\"rg-ssr\">", html);
        Assert.Contains("<h1>契约总裁的替身新娘</h1>", html);
        Assert.Contains("身份反转的逆袭故事。", html);
        Assert.Contains($"<a href=\"/video/{id}/ep-2\">第2集</a>", html);         // one URL per episode
        Assert.Contains("<div id=\"root\"></div>", html);                         // the app still mounts as before
        Assert.Contains("/assets/app.js", html);

        var (epStatus, ep, _) = await Page(api, $"/video/{id}/ep-2");
        Assert.Equal(HttpStatusCode.OK, epStatus);
        Assert.Contains("<title>契约总裁的替身新娘 第2集在线观看 - ", ep);
        Assert.Contains($"href=\"https://reelguo.example/video/{id}/ep-2\"", ep);
        Assert.Contains("property=\"og:type\" content=\"video.episode\"", ep);

        // The old ?ep= links point search engines at the episode URL; episode 1 is the series page itself.
        Assert.Contains($"<link rel=\"canonical\" href=\"https://reelguo.example/video/{id}/ep-3\" />", (await Page(api, $"/video/{id}?ep=3")).Body);
        Assert.Contains($"<link rel=\"canonical\" href=\"https://reelguo.example/video/{id}\" />", (await Page(api, $"/video/{id}/ep-1")).Body);
        Assert.Equal(HttpStatusCode.NotFound, (await Page(api, $"/video/{id}/ep-9")).Status);
    }

    [Fact]
    public async Task Content_follows_licence_territories_the_block_list_and_unknown_paths()
    {
        await using var api = Api(indexing: true);
        var jpOnly = await SeedAsync(api, "仅日本可看");

        await using (var scope = api.Services.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ReelGuoDbContext>();
            var s = await ctx.Series.FindAsync([jpOnly], Ct);
            s!.Territories = ["JP"];
            await ctx.SaveChangesAsync(Ct);
        }

        Assert.Equal(HttpStatusCode.OK, (await Page(api, $"/video/{jpOnly}", "JP")).Status);
        var (status, html, _) = await Page(api, $"/video/{jpOnly}", "US");
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Contains("content=\"noindex,nofollow\"", html);
        Assert.DoesNotContain("仅日本可看", html);

        // Blocked countries get the app without any content (the app shows why).
        var (blocked, blockedHtml, _) = await Page(api, "/", "CN");
        Assert.Equal(HttpStatusCode.UnavailableForLegalReasons, blocked);
        Assert.Contains("<div id=\"seo\" class=\"rg-ssr\"></div>", blockedHtml);

        var (missing, missingHtml, _) = await Page(api, "/no-such-page");
        Assert.Equal(HttpStatusCode.NotFound, missing);
        Assert.Contains("页面不存在", missingHtml);

        // API and media paths keep their own 404s.
        var api404 = await api.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/v1/nope").From("US"), Ct);
        Assert.Equal(HttpStatusCode.NotFound, api404.StatusCode);
        Assert.DoesNotContain("<html", await api404.Content.ReadAsStringAsync(Ct));

        // Private pages are never indexed.
        Assert.Contains("content=\"noindex,nofollow\"", (await Page(api, "/me")).Body);
    }

    [Fact]
    public async Task Text_from_the_catalogue_is_escaped_in_the_page_and_in_structured_data()
    {
        await using var api = Api(indexing: true);
        var id = await SeedAsync(api, "坏</script><script>alert(1)</script>剧");
        var (_, html, _) = await Page(api, $"/video/{id}");
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("<\\/script>", html);    // inside JSON-LD
    }

    [Fact]
    public async Task Indexing_is_off_by_default_and_the_sitemap_lists_series_and_episodes_when_on()
    {
        await using (var closed = Api())
        {
            var robots = await closed.CreateClient().GetStringAsync("/robots.txt", Ct);
            Assert.Contains("Disallow: /", robots);
            Assert.DoesNotContain("Sitemap:", robots);
            var id = await SeedAsync(closed, "测试剧");
            var (_, html, response) = await Page(closed, $"/video/{id}");
            Assert.Contains("content=\"noindex,nofollow\"", html);
            Assert.Equal("noindex, nofollow", response.Headers.GetValues("X-Robots-Tag").Single());
        }

        await using var open = Api(indexing: true);
        var seriesId = await SeedAsync(open, "上榜剧");
        var client = open.CreateClient();
        var robotsOn = await client.GetStringAsync("/robots.txt", Ct);
        Assert.Contains("Sitemap: https://reelguo.example/sitemap.xml", robotsOn);
        Assert.Contains("Disallow: /api/", robotsOn);

        var index = await client.GetStringAsync("/sitemap.xml", Ct);
        Assert.Contains("<loc>https://reelguo.example/sitemaps/pages.xml</loc>", index);
        Assert.Contains("<loc>https://reelguo.example/sitemaps/series-1.xml</loc>", index);

        var pages = await client.GetStringAsync("/sitemaps/pages.xml", Ct);
        Assert.Contains("<loc>https://reelguo.example/</loc>", pages);
        Assert.Contains("<loc>https://reelguo.example/ranks/hot</loc>", pages);

        // The series file holds every visible series (possibly over several files on a shared test database).
        var found = false;
        for (var n = 1; n <= 50 && !found; n++)
        {
            var file = await client.GetAsync($"/sitemaps/series-{n}.xml", Ct);
            if (file.StatusCode == HttpStatusCode.NotFound) break;
            var xml = await file.Content.ReadAsStringAsync(Ct);
            found = xml.Contains($"<loc>https://reelguo.example/video/{seriesId}</loc>") && xml.Contains($"<loc>https://reelguo.example/video/{seriesId}/ep-3</loc>");
        }
        Assert.True(found, "the series and its episode URLs are in a sitemap file");
    }

    [Fact]
    public async Task Without_a_template_page_paths_are_not_answered()
    {
        await using var api = db.CreateApi();
        var response = await api.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Get, "/topics").From("US"), Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
