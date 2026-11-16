using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using ReelGuo.Data.Media;
using static ReelGuo.Api.Tests.HttpTestExtensions;

namespace ReelGuo.Api.Tests;

/// <summary>Tencent COS signing, key layout and settings: no database, no network.</summary>
public sealed class CosVideoUnitTests
{
    /// <summary>The example key from Tencent's signature documentation (public, not a credential).</summary>
    private const string DocSecretKey = "BQYIM75p8x0iWVFSIgqEKwFprpRSVHlz";

    [Fact]
    public void Signature_matches_the_documented_worked_example()
    {
        var url = CosSigner.Presign("put", "host", "/exampleobject", new CosCredentials("AKIDEXAMPLE", DocSecretKey),
            DateTimeOffset.FromUnixTimeSeconds(1557989151), DateTimeOffset.FromUnixTimeSeconds(1557996351));
        var q = HttpUtility.ParseQueryString(new Uri(url).Query);

        Assert.Equal("1557989151;1557996351", q["q-sign-time"]);
        Assert.Equal("fe1df45b0052afe529a937d5cdd98c003f1c3e20", q["q-signature"]);
    }

    [Fact]
    public void A_real_file_name_is_signed_decoded_and_sent_percent_encoded()
    {
        const string key = "/videos/self/(Dubbed) 8-POUND BEEF，I'M DONE/8-POUND BEEF_1.mp4";
        var url = new Uri(CosSigner.Presign("put", "host", key, new CosCredentials("AKIDEXAMPLE", DocSecretKey),
            DateTimeOffset.FromUnixTimeSeconds(1557989151), DateTimeOffset.FromUnixTimeSeconds(1557996351)));

        Assert.DoesNotContain(" ", url.OriginalString);
        Assert.Equal(key, Uri.UnescapeDataString(url.AbsolutePath));
        // The signature is over the decoded key, as COS checks it.
        Assert.Equal(Sign("put", key), HttpUtility.ParseQueryString(url.Query)["q-signature"]);
    }

    [Fact]
    public void The_method_is_signed_so_a_playback_link_cannot_upload()
    {
        var cert = new CosCredentials("AKIDEXAMPLE", DocSecretKey);
        var get = HttpUtility.ParseQueryString(new Uri(CosSigner.Presign("get", "h", "/o", cert, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1))).Query);
        var put = HttpUtility.ParseQueryString(new Uri(CosSigner.Presign("put", "h", "/o", cert, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1))).Query);
        Assert.NotEqual(get["q-signature"], put["q-signature"]);
    }

    [Fact]
    public void Keys_mirror_the_folder_on_the_pc()
    {
        Assert.Equal("videos/self/(Dubbed) Betrayed/Betrayed_12.mp4", CosKeys.EpisodeKey("self", "(Dubbed) Betrayed", "Betrayed_12.mp4"));
        Assert.Equal("videos/self/Betrayed/Betrayed_12~5-12.mp4", CosKeys.EpisodeKey("self", "Betrayed", "Betrayed_12.mp4", "5-12"));
        // Slashes, control characters and edge dots never make extra folders.
        Assert.Equal("videos/ab/_/x.mp4", CosKeys.EpisodeKey(" a/b ", "..", "x.mp4"));
        Assert.Equal("_", CosKeys.Segment("../"));
        Assert.True(CosKeys.IsCosKey("videos/self/a/b.mp4"));
        Assert.False(CosKeys.IsCosKey("uploads/1/abc/b.mp4"));
        Assert.Null(CosKeys.FolderName("a/b/" + new string('x', 70)));
        Assert.Equal("self", CosKeys.FolderName(" self "));
    }

    [Fact]
    public void Titles_match_across_dubbed_prefix_case_and_punctuation()
    {
        Assert.Equal(CosKeys.NameKey("8-POUND BEEF，I'M DONE SERVING YOU!"), CosKeys.NameKey("8-Pound Beef, I'm Done Serving You"));
        Assert.Equal(CosKeys.NameKey("(Dubbed) Betrayed by Him"), CosKeys.NameKey("Betrayed by Him"));
        Assert.Equal("被背叛的她", CosKeys.NameKey("被背叛的她！"));
        Assert.StartsWith("n-", CosKeys.DerivedId(CosKeys.NameKey("Betrayed by Him")));
    }

    [Theory]
    [InlineData("Betrayed_12.mp4", 12)]
    [InlineData("Betrayed_12-da6a970674f2.mp4.qkdownloading", 12)]
    [InlineData("8-POUND BEEF_1.mp4", 1)]
    [InlineData("Betrayed.mp4", null)]
    [InlineData("Betrayed_0.mp4", null)]
    public void Episode_numbers_come_from_the_file_name(string name, int? expected) =>
        Assert.Equal(expected, CosKeys.EpisodeNumber(name));

    [Fact]
    public void Half_configured_cos_refuses_to_start_and_says_what_is_missing()
    {
        Assert.Null(CosVideoStore.Problem(new VideoOptions()));
        var missing = CosVideoStore.Problem(new VideoOptions { Provider = "Cos" });
        Assert.Contains("Video:Cos:SecretId", missing);
        Assert.Contains("Video:Cos:Bucket", missing);
        Assert.Contains("APPID", CosVideoStore.Problem(Cos(o => o.Bucket = "reelguo")));
        Assert.Null(CosVideoStore.Problem(Cos(_ => { })));
    }

    [Fact]
    public void Playback_links_expire_and_use_the_cdn_host_when_set()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-16T00:00:00Z"));
        var store = new CosVideoStore(Cos(o => o.PlaybackUrlMinutes = 30), clock, new HttpClient());
        var (url, expires) = store.PlaybackUrl("videos/self/T/T_1.mp4");
        Assert.StartsWith("https://reelguo-1300000000.cos.ap-singapore.myqcloud.com/videos/self/T/T_1.mp4?", url);
        Assert.Equal(clock.GetUtcNow().AddMinutes(30), expires);
        Assert.EndsWith($";{clock.GetUtcNow().AddMinutes(30).ToUnixTimeSeconds()}", HttpUtility.ParseQueryString(new Uri(url).Query)["q-sign-time"]);

        var cdn = new CosVideoStore(Cos(o => o.CdnHost = "v.example.com"), clock, new HttpClient());
        Assert.StartsWith("https://v.example.com/videos/", cdn.PlaybackUrl("videos/a/b.mp4").Url);
        // Uploads and the worker's reads always go to the bucket itself.
        Assert.StartsWith("https://reelguo-1300000000.cos.ap-singapore.myqcloud.com/", cdn.UploadUrl("videos/a/b.mp4"));
        Assert.StartsWith("https://reelguo-1300000000.cos.ap-singapore.myqcloud.com/", cdn.ReadUrl("videos/a/b.mp4", TimeSpan.FromMinutes(5)));
    }

    private static VideoOptions Cos(Action<CosOptions> change)
    {
        var o = new VideoOptions { Provider = "Cos", Cos = { SecretId = "AKIDtest", SecretKey = "secret", Bucket = "reelguo-1300000000", Region = "ap-singapore" } };
        change(o.Cos);
        return o;
    }

    private static string Sign(string method, string path)
    {
        const string keyTime = "1557989151;1557996351";
        static string Hex(byte[] b) => Convert.ToHexStringLower(b);
        var signKey = Hex(System.Security.Cryptography.HMACSHA1.HashData(Encoding.UTF8.GetBytes(DocSecretKey), Encoding.UTF8.GetBytes(keyTime)));
        var toSign = $"sha1\n{keyTime}\n{Hex(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes($"{method}\n{path}\n\n\n")))}\n";
        return Hex(System.Security.Cryptography.HMACSHA1.HashData(Encoding.UTF8.GetBytes(signKey), Encoding.UTF8.GetBytes(toSign)));
    }
}

/// <summary>
/// A stand-in for a COS bucket on 127.0.0.1: PUT, HEAD, ranged GET and the bucket listing, enough for the browser's
/// upload, the API's checks, the worker's ffprobe and a viewer's player. Signatures are required but not verified.
/// </summary>
public sealed class FakeCos : IAsyncDisposable
{
    private readonly WebApplication _app;

    public ConcurrentDictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
    public string Endpoint { get; }

    private FakeCos(WebApplication app, string endpoint)
    {
        _app = app;
        Endpoint = endpoint;
    }

    public static async Task<FakeCos> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        FakeCos? self = null;
        app.Run(ctx => self!.HandleAsync(ctx));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        self = new FakeCos(app, address);
        return self;
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        if (string.IsNullOrEmpty(ctx.Request.Query["q-signature"]))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var key = Uri.UnescapeDataString(ctx.Request.Path.Value!.TrimStart('/'));
        if (HttpMethods.IsPut(ctx.Request.Method))
        {
            using var body = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(body);
            Objects[key] = body.ToArray();
            return;
        }
        if (key.Length == 0 && HttpMethods.IsGet(ctx.Request.Method))
        {
            await ListAsync(ctx);
            return;
        }
        if (!Objects.TryGetValue(key, out var bytes))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (HttpMethods.IsHead(ctx.Request.Method))
        {
            ctx.Response.ContentLength = bytes.Length;
            return;
        }
        await Results.Bytes(bytes, "video/mp4", enableRangeProcessing: true).ExecuteAsync(ctx);
    }

    private async Task ListAsync(HttpContext ctx)
    {
        var prefix = ctx.Request.Query["prefix"].ToString();
        var delimiter = ctx.Request.Query["delimiter"].ToString();
        var keys = Objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><ListBucketResult><IsTruncated>false</IsTruncated>");
        if (delimiter == "/")
        {
            foreach (var p in keys.Select(k => k[prefix.Length..]).Where(r => r.Contains('/')).Select(r => prefix + r[..(r.IndexOf('/') + 1)]).Distinct())
                xml.Append($"<CommonPrefixes><Prefix>{System.Security.SecurityElement.Escape(p)}</Prefix></CommonPrefixes>");
        }
        else
        {
            foreach (var k in keys)
                xml.Append($"<Contents><Key>{System.Security.SecurityElement.Escape(k)}</Key><Size>{Objects[k].Length}</Size></Contents>");
        }
        xml.Append("</ListBucketResult>");
        ctx.Response.ContentType = "application/xml";
        await ctx.Response.WriteAsync(xml.ToString());
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

/// <summary>The folder upload against a COS bucket, end to end: plan, create, upload, link, confirm, process, publish, play.</summary>
public sealed class CosLibraryTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Folder_upload_to_cos_then_play_from_cos()
    {
        FfmpegLocator.RequireOrSkip();
        await using var cos = await FakeCos.StartAsync();
        await using var api = db.CreateApi(new Dictionary<string, string?>
        {
            ["Video:Provider"] = "Cos",
            ["Video:Cos:SecretId"] = "AKIDtest",
            ["Video:Cos:SecretKey"] = "test-secret",
            ["Video:Cos:Bucket"] = "reelguo-1300000000",
            ["Video:Cos:Region"] = "ap-singapore",
            ["Video:Cos:Folder"] = "reelguo",
            ["Video:Cos:Endpoint"] = cos.Endpoint,
        });
        var admin = await AdminClient.SignInAsync(api);
        using var direct = new HttpClient();

        const string folderName = "(Dubbed) Betrayed Title";
        var clips = new List<string>();
        for (var n = 1; n <= 2; n++)
            clips.Add(await FfmpegLocator.MakeClipAsync(Path.Combine(api.StorageRoot, $"Betrayed Title_{n}.mp4"), 540, 960, 2));
        // Episode 2 is already in the bucket where this upload would put it (as when the Viora platform shares it).
        cos.Objects[$"videos/self/{folderName}/Betrayed Title_2.mp4"] = await File.ReadAllBytesAsync(clips[1], Ct);
        // Someone else's file under the default folder, which a per-episode upload must not overwrite.
        cos.Objects["videos/reelguo/被背叛的她/Betrayed Title_3.mp4"] = [1, 2, 3];

        var config = await admin.GetAsync("/api/admin/library", Ct).OkJsonAsync();
        Assert.True(config.GetProperty("cos").GetBoolean());
        Assert.Equal(["reelguo", "self"], config.GetProperty("folders").EnumerateArray().Select(f => f.GetString()));

        object Mp4(int n, bool complete = true) => new
        {
            name = $"Betrayed Title_{n}.mp4", bytes = n <= 2 ? new FileInfo(clips[n - 1]).Length : 1234, duration_sec = 2.0,
            width = 540, height = 960, complete, faststart = true, video_codec = "avc1", audio_codec = "mp4a",
        };
        var details = JsonSerializer.Serialize(new
        {
            id = "rp-001", title = new { en = "(Dubbed) Betrayed Title", zh = "被背叛的她" },
            synopsis = new { zh = "测试简介" }, genres = new[] { "urban" }, total_episodes = 3,
        });
        var listing = new
        {
            details = new[] { new { name = "Betrayed Title.json", text = details } },
            images = Array.Empty<object>(),
            folders = new[] { new { name = folderName, files = new[] { Mp4(1), Mp4(2), Mp4(3, complete: false) }, downloading = new[] { "Betrayed Title_4-da6a970674f2.mp4.qkdownloading" } } },
        };

        // Plan: one title from three folders, episode 2 found in the bucket, episode 3 still arriving.
        var plan = await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/library/plan", new { storage_folder = "self", listing }).OkJsonAsync();
        var title = plan.GetProperty("titles").EnumerateArray().Single();
        Assert.Equal(("new", "被背叛的她", "rp-001"), (title.GetProperty("status").GetString(), title.GetProperty("title").GetString(), title.GetProperty("external_id").GetString()));
        Assert.Equal(["new", "stored", "incomplete"], title.GetProperty("episodes").EnumerateArray().Select(e => e.GetProperty("state").GetString()));
        Assert.Equal(1, title.GetProperty("downloading").GetInt32());

        // Create the draft series, with the rights details the page was given.
        var applied = await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/library/titles", new
        {
            category_slug = "ai-duanju",
            listing,
            rights = new { rights_holder = "Own Studio", contract_ref = "OWN-1", licence_start = "2026-01-01", territories = new[] { "*" }, ai_generated = true, has_real_person = false },
        }).OkJsonAsync();
        Assert.True(applied.GetProperty("created").GetBoolean());
        var seriesId = applied.GetProperty("series_id").GetInt64();

        // Episode 1: uploaded straight to the bucket, under the PC's own names.
        var one = await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/library/episodes", new
        {
            series_id = seriesId, number = 1, storage_folder = "self", title_folder = folderName, file_name = "Betrayed Title_1.mp4",
            bytes = new FileInfo(clips[0]).Length, content_type = "video/mp4", replace = false,
        }).OkJsonAsync();
        Assert.False(one.GetProperty("stored").GetBoolean());
        var putUrl = one.GetProperty("upload").GetProperty("url").GetString()!;
        Assert.StartsWith(cos.Endpoint + "/videos/self/%28Dubbed%29%20Betrayed%20Title/Betrayed%20Title_1.mp4?", putUrl);
        var put = new HttpRequestMessage(HttpMethod.Put, putUrl) { Content = new ByteArrayContent(await System.IO.File.ReadAllBytesAsync(clips[0], Ct)) };
        put.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        Assert.True((await direct.SendAsync(put, Ct)).IsSuccessStatusCode);
        var oneId = one.GetProperty("episode_id").GetInt64();
        var wrong = await admin.AdminSendAsync(HttpMethod.Post, $"/api/admin/library/episodes/{oneId}/commit", new { bytes = 1 });
        Assert.Equal("size_mismatch", await wrong.ErrorCodeAsync());
        await admin.AdminSendAsync(HttpMethod.Post, $"/api/admin/library/episodes/{oneId}/commit", new { bytes = new FileInfo(clips[0]).Length }).OkJsonAsync();

        // Episode 2: already there with the same size, so it is linked and nothing is sent.
        var two = await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/library/episodes", new
        {
            series_id = seriesId, number = 2, storage_folder = "self", title_folder = folderName, file_name = "Betrayed Title_2.mp4",
            bytes = new FileInfo(clips[1]).Length, content_type = "video/mp4", replace = false,
        }).OkJsonAsync();
        Assert.True(two.GetProperty("stored").GetBoolean());
        Assert.Equal(JsonValueKind.Null, two.GetProperty("upload").ValueKind);
        await admin.AdminSendAsync(HttpMethod.Post, $"/api/admin/library/episodes/{two.GetProperty("episode_id").GetInt64()}/commit",
            new { bytes = new FileInfo(clips[1]).Length }).OkJsonAsync();

        // The worker measures each file in COS and takes a poster frame; nothing is re-encoded.
        Assert.True(await AdminClient.RunWorkerOnceAsync(api));
        Assert.True(await AdminClient.RunWorkerOnceAsync(api));
        var detail = await admin.GetAsync($"/api/admin/series/{seriesId}", Ct).OkJsonAsync();
        var episodes = detail.GetProperty("episodes").EnumerateArray().ToList();
        Assert.All(episodes, e => Assert.Equal("ready", e.GetProperty("status").GetString()));
        Assert.All(episodes, e => Assert.Equal((540, 960), (e.GetProperty("width").GetInt32(), e.GetProperty("height").GetInt32())));
        Assert.InRange(episodes[0].GetProperty("duration").GetDouble(), 1.5, 2.5);
        Assert.Equal(0, detail.GetProperty("publish_problems").GetArrayLength());

        // Planning again: both on the site, nothing left to send.
        plan = await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/library/plan", new { storage_folder = "self", listing }).OkJsonAsync();
        title = plan.GetProperty("titles").EnumerateArray().Single();
        Assert.Equal(seriesId, title.GetProperty("series_id").GetInt64());
        Assert.Equal(["uploaded", "uploaded", "incomplete"], title.GetProperty("episodes").EnumerateArray().Select(e => e.GetProperty("state").GetString()));

        // A viewer plays it straight from the bucket, with a signed link that honours range requests.
        await admin.AdminSendAsync(HttpMethod.Post, $"/api/admin/series/{seriesId}/publish").OkJsonAsync();
        var viewer = api.CreateClient();
        var play = await viewer.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/series/{seriesId}/episodes/2/play").From("US"), Ct).OkJsonAsync();
        Assert.Equal("mp4", play.GetProperty("kind").GetString());
        var videoUrl = play.GetProperty("video_url").GetString()!;
        Assert.StartsWith(cos.Endpoint + "/videos/self/", videoUrl);
        var ranged = new HttpRequestMessage(HttpMethod.Get, videoUrl);
        ranged.Headers.Range = new RangeHeaderValue(0, 99);
        var partial = await direct.SendAsync(ranged, Ct);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(100, (await partial.Content.ReadAsByteArrayAsync(Ct)).Length);
        Assert.Equal(HttpStatusCode.UnavailableForLegalReasons,
            (await viewer.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/series/{seriesId}/episodes/2/play").From("CN"), Ct)).StatusCode);

        // The episode editor's upload uses the same layout under the default folder, and never overwrites a file
        // somebody else put there.
        var created = await admin.AdminSendAsync(HttpMethod.Post, $"/api/admin/series/{seriesId}/episodes",
            new { file_name = "Betrayed Title_3.mp4", content_type = "video/mp4", title = (string?)null, episode_number = 3 }).OkJsonAsync();
        var editorUrl = created.GetProperty("upload").GetProperty("url").GetString()!;
        Assert.StartsWith(cos.Endpoint + "/videos/reelguo/", editorUrl);
        Assert.Contains($"Betrayed%20Title_3~{seriesId}-3.mp4?", editorUrl);
    }
}
