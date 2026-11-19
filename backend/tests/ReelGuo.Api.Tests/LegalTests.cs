using System.Net;
using ReelGuo.Api.Legal;
using static ReelGuo.Api.Tests.HttpTestExtensions;

namespace ReelGuo.Api.Tests;

/// <summary>Legal pages: full text for everyone, with the 待法务审核 notice on until an admin turns it off.</summary>
public sealed class LegalTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    private static Task<HttpResponseMessage> Get(HttpClient client, string url, string country = "US") =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Get, url).From(country), Ct);

    [Fact]
    public async Task Every_page_shows_its_full_text_with_the_review_notice_by_default()
    {
        await using var api = db.CreateApi();
        var viewer = api.CreateClient();
        foreach (var page in LegalPages.All)
        {
            var dto = await Get(viewer, $"/api/legal/{page.Slug}").OkJsonAsync();
            Assert.Equal((page.Path, page.Title), (dto.GetProperty("path").GetString(), dto.GetProperty("title").GetString()));
            Assert.True(dto.GetProperty("under_review").GetBoolean());
            var html = dto.GetProperty("html").GetString()!;
            Assert.Contains("<h2>", html);
            Assert.Contains("黄月短剧", html);          // {{site}} filled in
            Assert.DoesNotContain("{{site}}", html);
            Assert.True(html.Length > 800, $"{page.Slug}: only {html.Length} characters");
        }
        Assert.Contains("仅限年满 18 周岁", (await Get(viewer, "/api/legal/terms").OkJsonAsync()).GetProperty("html").GetString());
        Assert.Contains("不提供任何", (await Get(viewer, "/api/legal/content-policy").OkJsonAsync()).GetProperty("html").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await Get(viewer, "/api/legal/nope")).StatusCode);
    }

    [Fact]
    public async Task Admins_turn_the_notice_off_and_on_and_blocked_regions_still_read_the_pages()
    {
        await using var api = db.CreateApi();
        var admin = await AdminClient.SignInAsync(api);
        var viewer = api.CreateClient();

        var list = await admin.AdminSendAsync(HttpMethod.Get, "/api/admin/legal").OkJsonAsync();
        Assert.Equal(LegalPages.All.Count, list.GetArrayLength());
        Assert.All(list.EnumerateArray(), p => Assert.True(p.GetProperty("under_review").GetBoolean()));

        var off = await admin.AdminSendAsync(HttpMethod.Put, "/api/admin/legal/privacy", new { under_review = false }).OkJsonAsync();
        Assert.False(off.GetProperty("under_review").GetBoolean());
        Assert.StartsWith("admin:", off.GetProperty("updated_by").GetString());
        Assert.False((await Get(viewer, "/api/legal/privacy").OkJsonAsync()).GetProperty("under_review").GetBoolean());
        Assert.True((await Get(viewer, "/api/legal/terms").OkJsonAsync()).GetProperty("under_review").GetBoolean());

        await admin.AdminSendAsync(HttpMethod.Put, "/api/admin/legal/privacy", new { under_review = true }).OkJsonAsync();
        Assert.True((await Get(viewer, "/api/legal/privacy").OkJsonAsync()).GetProperty("under_review").GetBoolean());

        Assert.Equal(HttpStatusCode.NotFound, (await admin.AdminSendAsync(HttpMethod.Put, "/api/admin/legal/nope", new { under_review = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await viewer.SendAsync(
            new HttpRequestMessage(HttpMethod.Put, "/api/admin/legal/terms") { Content = System.Net.Http.Json.JsonContent.Create(new { under_review = false }) }.WithCsrf(), Ct)).StatusCode);

        // A region the site is blocked in can still read what it agreed to.
        await admin.AdminSendAsync(HttpMethod.Put, "/api/admin/geo/blocked-countries/DE", new { note = "test" }).OkJsonAsync();
        Assert.Equal(HttpStatusCode.UnavailableForLegalReasons, (await Get(viewer, "/api/v1/config", "DE")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get(viewer, "/api/legal/terms", "DE")).StatusCode);
        await admin.AdminSendAsync(HttpMethod.Delete, "/api/admin/geo/blocked-countries/DE");
    }
}
