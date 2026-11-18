using System.Net;
using static ReelGuo.Api.Tests.HttpTestExtensions;

namespace ReelGuo.Api.Tests;

/// <summary>下载页图片: uploads replace a slot's default, replacing or resetting removes the old file.</summary>
public sealed class LandingImagesTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1, 1, 0, 0];

    private static Task<HttpResponseMessage> Get(HttpClient client, string url, string country = "US") =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Get, url).From(country), Ct);

    [Fact]
    public async Task Uploaded_images_replace_the_defaults_and_can_be_reset()
    {
        await using var api = db.CreateApi();
        var admin = await AdminClient.SignInAsync(api);
        var viewer = api.CreateClient();
        foreach (var slot in new[] { "creator_d", "genre_2" }) await admin.AdminSendAsync(HttpMethod.Delete, $"/api/admin/landing-images/{slot}");

        Task<HttpResponseMessage> Upload(HttpClient client, string slot, byte[] bytes) =>
            client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/admin/landing-images/{slot}") { Content = new ByteArrayContent(bytes) }.WithCsrf(), Ct);
        string FileOf(string url) => Path.Combine(api.StorageRoot, "media", "p", "landing", url[(url.LastIndexOf('/') + 1)..]);
        async Task<Dictionary<string, string>> PublicImages() =>
            (await Get(viewer, "/api/v1/app/landing-images").OkJsonAsync()).GetProperty("images").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);

        Assert.False((await PublicImages()).ContainsKey("creator_d"));

        var first = (await Upload(admin, "creator_d", Png).OkJsonAsync()).GetProperty("url").GetString()!;
        Assert.Contains("/p/landing/creator_d-", first);
        Assert.EndsWith(".png", first);
        Assert.True(File.Exists(FileOf(first)));

        // Replacing gives a new address (no stale caches) and removes the old file.
        var second = (await Upload(admin, "creator_d", Jpeg).OkJsonAsync()).GetProperty("url").GetString()!;
        Assert.EndsWith(".jpg", second);
        Assert.NotEqual(first, second);
        Assert.False(File.Exists(FileOf(first)));
        await Upload(admin, "genre_2", Png).OkJsonAsync();

        var images = await PublicImages();
        Assert.Equal(second, images["creator_d"]);
        Assert.True(images.ContainsKey("genre_2"));
        var listed = await admin.AdminSendAsync(HttpMethod.Get, "/api/admin/landing-images").OkJsonAsync();
        var row = listed.EnumerateArray().Single(i => i.GetProperty("slot").GetString() == "creator_d");
        Assert.Equal(second, row.GetProperty("url").GetString());
        Assert.StartsWith("admin:", row.GetProperty("updated_by").GetString());

        // 恢复默认: the slot is empty again and its file is gone; resetting an empty slot is fine.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.AdminSendAsync(HttpMethod.Delete, "/api/admin/landing-images/creator_d")).StatusCode);
        Assert.False((await PublicImages()).ContainsKey("creator_d"));
        Assert.False(File.Exists(FileOf(second)));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.AdminSendAsync(HttpMethod.Delete, "/api/admin/landing-images/creator_d")).StatusCode);

        // Unknown slots, files that are not JPEG/PNG/WebP and visitors are refused.
        Assert.Equal(HttpStatusCode.NotFound, (await Upload(admin, "banner", Png)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.AdminSendAsync(HttpMethod.Delete, "/api/admin/landing-images/banner")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(admin, "home_d", "GIF89a......."u8.ToArray())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(admin, "home_d", [])).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Upload(viewer, "home_d", Png)).StatusCode);

        await admin.AdminSendAsync(HttpMethod.Delete, "/api/admin/landing-images/genre_2");
    }
}
