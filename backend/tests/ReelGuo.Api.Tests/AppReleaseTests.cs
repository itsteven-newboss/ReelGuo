using System.Net;
using System.Text.Json;
using ReelGuo.Api.Public;
using static ReelGuo.Api.Tests.HttpTestExtensions;

namespace ReelGuo.Api.Tests;

/// <summary>Phase 6: native app releases, update checks, the desktop updater feed and mirrors.</summary>
public sealed class AppReleaseTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData("0.6.0", "0.6.0")]
    [InlineData("v1.2", "1.2.0")]
    [InlineData("1.0.0-beta.1", "1.0.0")]
    [InlineData("2", "2.0.0")]
    public void Versions_parse_like_the_apps_report_them(string input, string expected) =>
        Assert.Equal(expected, AppVersions.Parse(input)!.ToString(3));

    [Theory]
    [InlineData("")]
    [InlineData("1.2.3.4")]
    [InlineData("one")]
    [InlineData("1..2")]
    public void Bad_versions_are_rejected(string input) => Assert.Null(AppVersions.Parse(input));

    [Fact]
    public void Versions_compare_numerically() => Assert.True(AppVersions.Parse("0.10.0") > AppVersions.Parse("0.9.3"));

    private static Task<JsonElement> Release(HttpClient admin, string platform, string version, bool published = true, bool mandatory = false, string? signature = "c2lnbmF0dXJl") =>
        admin.AdminSendAsync(HttpMethod.Post, "/api/admin/app-releases", new
        {
            platform, version, url = $"https://downloads.example.com/{platform}/{version}", signature, sha256 = (string?)null,
            notes = $"{version} 更新说明", mandatory, published,
        }).OkJsonAsync();

    private static Task<HttpResponseMessage> Get(HttpClient client, string url, string country = "US") =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Get, url).From(country), Ct);

    [Fact]
    public async Task Apps_learn_about_updates_and_mirrors_and_the_desktop_updater_gets_signed_releases()
    {
        await using var api = db.CreateApi(new Dictionary<string, string?>
        {
            ["App:Mirrors:0"] = "https://app.example.com/", ["App:Mirrors:1"] = "https://app2.example.net",
        });
        var admin = await AdminClient.SignInAsync(api);
        var client = api.CreateClient();

        await Release(admin, "android", "0.6.0", signature: null);
        await Release(admin, "android", "0.7.0", published: false, signature: null);   // not offered yet
        await Release(admin, "windows-x86_64", "0.6.0");

        var bootstrap = await Get(client, "/api/v1/app/bootstrap?platform=android&version=0.5.2").OkJsonAsync();
        Assert.Equal(["https://app.example.com", "https://app2.example.net"], bootstrap.GetProperty("mirrors").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal("0.6.0", bootstrap.GetProperty("latest").GetProperty("version").GetString());
        Assert.True(bootstrap.GetProperty("update_available").GetBoolean());
        Assert.False(bootstrap.GetProperty("update_required").GetBoolean());
        Assert.False((await Get(client, "/api/v1/app/bootstrap?platform=android&version=0.6.0").OkJsonAsync()).GetProperty("update_available").GetBoolean());

        // A mandatory release makes older installs update first.
        await Release(admin, "android", "0.6.1", mandatory: true, signature: null);
        var required = await Get(client, "/api/v1/app/bootstrap?platform=android&version=0.6.0").OkJsonAsync();
        Assert.True(required.GetProperty("update_required").GetBoolean());
        Assert.Equal("0.6.1", required.GetProperty("latest").GetProperty("version").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(client, "/api/v1/app/bootstrap?platform=ios&version=1.0.0")).StatusCode);

        // The Tauri updater: 200 with the signed bundle when newer, 204 otherwise.
        var update = await Get(client, "/api/v1/app/updates/windows/x86_64/0.5.0").OkJsonAsync();
        Assert.Equal("0.6.0", update.GetProperty("version").GetString());
        Assert.Equal("c2lnbmF0dXJl", update.GetProperty("signature").GetString());
        Assert.Equal("https://downloads.example.com/windows-x86_64/0.6.0", update.GetProperty("url").GetString());
        Assert.True(update.TryGetProperty("pub_date", out _));
        Assert.Equal(HttpStatusCode.NoContent, (await Get(client, "/api/v1/app/updates/windows/x86_64/0.6.0")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Get(client, "/api/v1/app/updates/darwin/aarch64/0.1.0")).StatusCode);

        // The download page lists the newest published build per platform.
        var latest = (await Get(client, "/api/v1/app/releases").OkJsonAsync()).EnumerateArray()
            .Select(r => $"{r.GetProperty("platform").GetString()} {r.GetProperty("version").GetString()}").ToList();
        Assert.Equal(["windows-x86_64 0.6.0", "android 0.6.1"], latest);

        // Update checks run before the web app loads, so strict-age countries don't gate them (blocked countries still do).
        await admin.AdminSendAsync(HttpMethod.Put, "/api/admin/geo/age-rules/GB", new { mode = "strict", note = "test" }).OkJsonAsync();
        Assert.Equal(HttpStatusCode.OK, (await Get(client, "/api/v1/app/bootstrap?platform=android&version=0.6.0", "GB")).StatusCode);
        Assert.Equal(HttpStatusCode.UnavailableForLegalReasons, (await Get(client, "/api/v1/app/bootstrap?platform=android&version=0.6.0", "CN")).StatusCode);
    }

    [Fact]
    public async Task Releases_are_validated_and_audited()
    {
        await using var api = db.CreateApi();
        var admin = await AdminClient.SignInAsync(api);

        async Task<string?> Rejected(object body)
        {
            var r = await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/app-releases", body);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            return await r.ErrorCodeAsync();
        }
        object Body(string platform = "android", string version = "1.0.0", string url = "https://d.example.com/a.apk", string? signature = null, bool published = true) =>
            new { platform, version, url, signature, sha256 = (string?)null, notes = "", mandatory = false, published };

        Assert.Equal("invalid_release", await Rejected(Body(platform: "ios")));
        Assert.Equal("invalid_release", await Rejected(Body(version: "latest")));
        Assert.Equal("invalid_release", await Rejected(Body(url: "http://d.example.com/a.apk")));
        Assert.Equal("invalid_release", await Rejected(Body(platform: "windows-x86_64")));   // published desktop build without a signature

        var draft = await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/app-releases", Body(platform: "windows-x86_64", published: false)).OkJsonAsync();
        var id = draft.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Conflict,
            (await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/app-releases", Body(platform: "windows-x86_64", version: "v1.0", published: false))).StatusCode);
        var published = await admin.AdminSendAsync(HttpMethod.Put, $"/api/admin/app-releases/{id}", Body(platform: "windows-x86_64", signature: "sig", published: true)).OkJsonAsync();
        Assert.True(published.GetProperty("published").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, published.GetProperty("published_at").ValueKind);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.AdminSendAsync(HttpMethod.Delete, $"/api/admin/app-releases/{id}")).StatusCode);

        var audit = (await admin.GetAsync("/api/admin/audit-log", Ct).OkJsonAsync()).EnumerateArray().Select(a => a.GetProperty("action").GetString()).ToList();
        Assert.Contains("app_release.create", audit);
        Assert.Contains("app_release.publish", audit);
        Assert.Contains("app_release.delete", audit);
    }
}
