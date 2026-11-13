using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ReelGuo.Data.Storage;
using ReelGuo.Worker;
using ReelGuo.Worker.Media;
using Testcontainers.PostgreSql;
using static ReelGuo.Api.Tests.HttpTestExtensions;

namespace ReelGuo.Api.Tests;

/// <summary>
/// A throwaway Postgres database per test class. Uses REELGUO_TEST_PG (a server connection string, e.g.
/// "Host=localhost;Username=reelguo;Password=reelguo") when set, otherwise starts a container via Docker.
/// Tests skip themselves when neither is available.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string? _serverConnection;
    private string? _database;

    public string? ConnectionString { get; private set; }
    public string? SkipReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        var server = Environment.GetEnvironmentVariable("REELGUO_TEST_PG");
        if (!string.IsNullOrWhiteSpace(server))
        {
            _serverConnection = server;
            _database = $"reelguo_test_{Guid.NewGuid():N}"[..30];
            ConnectionString = new NpgsqlConnectionStringBuilder(server) { Database = _database }.ConnectionString;
            return;
        }

        try
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
        }
        catch (Exception ex)
        {
            SkipReason = $"No database for tests ({ex.GetType().Name}): install Docker Desktop or set REELGUO_TEST_PG.";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
        if (_serverConnection is not null && _database is not null)
        {
            NpgsqlConnection.ClearAllPools();
            await using var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_serverConnection) { Database = "postgres" }.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public ApiFactory CreateApi(IReadOnlyDictionary<string, string?>? extra = null)
    {
        if (SkipReason is not null) Assert.Skip(SkipReason);
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = ConnectionString,
            ["Database:MigrateOnStartup"] = "true",
            ["Admin:BootstrapUsername"] = TestKeys.AdminUser,
            ["Admin:BootstrapPassword"] = TestKeys.AdminPassword,
        };
        if (extra is not null)
            foreach (var (k, v) in extra) settings[k] = v;
        return new ApiFactory(settings: settings, testAdmin: false);
    }
}

public static class AdminClient
{
    public static async Task<HttpClient> SignInAsync(ApiFactory api, string user = TestKeys.AdminUser, string password = TestKeys.AdminPassword)
    {
        var client = api.CreateClient();
        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/admin/auth/login")
        {
            Content = JsonContent.Create(new { username = user, password }),
        }.WithCsrf(), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    public static Task<HttpResponseMessage> AdminSendAsync(this HttpClient client, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url).WithCsrf();
        if (body is not null) request.Content = JsonContent.Create(body);
        return client.SendAsync(request, Ct);
    }

    public static async Task<JsonElement> OkJsonAsync(this Task<HttpResponseMessage> pending)
    {
        var response = await pending;
        if (!response.IsSuccessStatusCode)
            Assert.Fail($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return await response.JsonAsync();
    }

    /// <summary>Uploads a local file using the ticket the API returned (method, url, headers).</summary>
    public static async Task UploadAsync(this HttpClient client, JsonElement ticket, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(ticket.GetProperty("method").GetString()!), ticket.GetProperty("url").GetString())
        {
            Content = new ByteArrayContent(await File.ReadAllBytesAsync(path, Ct)),
        };
        foreach (var header in ticket.GetProperty("headers").EnumerateObject())
        {
            if (header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                request.Content.Headers.ContentType = new(header.Value.GetString()!);
            else
                request.Headers.Add(header.Name, header.Value.GetString());
        }
        var response = await client.SendAsync(request, Ct);
        Assert.True(response.IsSuccessStatusCode, $"upload failed: {(int)response.StatusCode}");
    }

    /// <summary>Runs one queued media job in-process with the real transcoder, as the worker would.</summary>
    public static Task<bool> RunWorkerOnceAsync(ApiFactory api)
    {
        var transcoder = new Transcoder(
            api.Services.GetRequiredService<StorageBuckets>(),
            new FfmpegOptions { BinDirectory = FfmpegLocator.BinDirectory ?? "" },
            NullLogger<Transcoder>.Instance);
        var runner = new JobRunner(api.Services.GetRequiredService<IServiceScopeFactory>(), transcoder, new WorkerOptions(),
            new WorkerStats(), TimeProvider.System, NullLogger<JobRunner>.Instance);
        return runner.RunOneAsync("test-worker", Ct);
    }

    public static object CompleteSeries(string title, string category = "ai-duanju", string[]? territories = null) => new
    {
        category_slug = category,
        title,
        description = "测试简介",
        tags = new[] { "都市", "逆袭" },
        score = 8.6,
        is_finished = false,
        is_original = true,
        total_episodes = 10,
        author_name = "测试工作室",
        rights_holder = "Test Studio Ltd",
        contract_ref = "C-2026-001",
        licence_start = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)).ToString("yyyy-MM-dd"),
        territories = territories ?? ["*"],
        ai_generated = true,
        has_real_person = false,
    };
}

public sealed class DatabaseTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Migrations_seed_the_four_channels_with_face_swap_as_coming_soon()
    {
        await using var api = db.CreateApi();
        var response = await api.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/v1/categories").From("US"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = (await response.JsonAsync()).EnumerateArray().ToList();
        Assert.Equal(["ai-duanju", "ai-manju", "ai-huanlian", "ai-mogai"], items.Select(i => i.GetProperty("slug").GetString()));
        Assert.Equal("coming_soon", items[2].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Mainland_china_is_blocked_by_default()
    {
        await using var api = db.CreateApi();
        var response = await api.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/v1/config").From("CN"), Ct);
        Assert.Equal(HttpStatusCode.UnavailableForLegalReasons, response.StatusCode);
    }

    [Fact]
    public async Task Login_logout_and_wrong_password()
    {
        await using var api = db.CreateApi();
        var anonymous = api.CreateClient();
        var wrong = await anonymous.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/admin/auth/login")
        {
            Content = JsonContent.Create(new { username = TestKeys.AdminUser, password = "nope-nope-nope" }),
        }.WithCsrf(), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("invalid_credentials", await wrong.ErrorCodeAsync());

        var client = await AdminClient.SignInAsync(api);
        Assert.Equal("admin:admin", (await client.GetAsync("/api/admin/session", Ct).OkJsonAsync()).GetProperty("actor").GetString());

        await client.AdminSendAsync(HttpMethod.Post, "/api/admin/auth/logout");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/session", Ct)).StatusCode);
    }

    [Fact]
    public async Task Disabling_an_admin_ends_their_session()
    {
        await using var api = db.CreateApi();
        var owner = await AdminClient.SignInAsync(api);
        var created = await owner.AdminSendAsync(HttpMethod.Post, "/api/admin/users",
            new { username = "legal01", nickname = "法务一号", password = "legal-password-1" }).OkJsonAsync();
        var legal = await AdminClient.SignInAsync(api, "legal01", "legal-password-1");
        Assert.Equal(HttpStatusCode.OK, (await legal.GetAsync("/api/admin/session", Ct)).StatusCode);

        var id = created.GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.AdminSendAsync(HttpMethod.Post, $"/api/admin/users/{id}/status", new { active = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await legal.GetAsync("/api/admin/session", Ct)).StatusCode);
    }

    [Fact]
    public async Task Admin_block_and_unblock_take_effect_immediately_and_are_audited()
    {
        await using var api = db.CreateApi();
        var client = await AdminClient.SignInAsync(api);
        HttpRequestMessage Config() => new HttpRequestMessage(HttpMethod.Get, "/api/v1/config").From("FR");

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Config(), Ct)).StatusCode);

        var block = await client.AdminSendAsync(HttpMethod.Put, "/api/admin/geo/blocked-countries/fr", new { note = "法务审核 #12" }).OkJsonAsync();
        Assert.Equal("FR", block.GetProperty("country_code").GetString());
        Assert.Equal(HttpStatusCode.UnavailableForLegalReasons, (await client.SendAsync(Config(), Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.AdminSendAsync(HttpMethod.Delete, "/api/admin/geo/blocked-countries/FR")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Config(), Ct)).StatusCode);

        var audit = await client.GetAsync("/api/admin/audit-log?target_type=blocked_country", Ct).OkJsonAsync();
        var entries = audit.EnumerateArray()
            .Where(e => e.GetProperty("target_id").GetString() == "FR")
            .Select(e => (Action: e.GetProperty("action").GetString(), Actor: e.GetProperty("actor").GetString()))
            .ToList();
        Assert.Equal([("geo.unblock", "admin:admin"), ("geo.block", "admin:admin")], entries);
    }

    [Fact]
    public async Task Coming_soon_channel_accepts_no_content()
    {
        await using var api = db.CreateApi();
        var client = await AdminClient.SignInAsync(api);
        var response = await client.AdminSendAsync(HttpMethod.Post, "/api/admin/series", AdminClient.CompleteSeries("换脸测试", "ai-huanlian"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("category_not_open", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task Publishing_lists_every_missing_requirement()
    {
        await using var api = db.CreateApi();
        var client = await AdminClient.SignInAsync(api);
        var series = await client.AdminSendAsync(HttpMethod.Post, "/api/admin/series",
            new { category_slug = "ai-duanju", title = "缺资料的剧", score = 0, total_episodes = 0 }).OkJsonAsync();

        var publish = await client.AdminSendAsync(HttpMethod.Post, $"/api/admin/series/{series.GetProperty("id").GetInt64()}/publish");
        Assert.Equal(HttpStatusCode.BadRequest, publish.StatusCode);
        var error = (await publish.JsonAsync()).GetProperty("error");
        Assert.Equal("publish_blocked", error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("details").GetArrayLength() >= 6);
    }
}

public sealed class PipelineTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Upload_transcode_publish_and_play_end_to_end()
    {
        FfmpegLocator.RequireOrSkip();
        await using var api = db.CreateApi();
        var admin = await AdminClient.SignInAsync(api);
        var clip = await FfmpegLocator.MakeClipAsync(Path.Combine(api.StorageRoot, "clip.mp4"), 720, 1280, 4);

        // Admin: series, episode, upload, process.
        var series = await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/series", AdminClient.CompleteSeries("霸道总裁的测试")).OkJsonAsync();
        var seriesId = series.GetProperty("id").GetInt64();
        var created = await admin.AdminSendAsync(HttpMethod.Post, $"/api/admin/series/{seriesId}/episodes",
            new { file_name = "第1集.mp4", content_type = "video/mp4", title = "初遇" }).OkJsonAsync();
        var episodeId = created.GetProperty("episode").GetProperty("id").GetInt64();
        await admin.UploadAsync(created.GetProperty("upload"), clip);
        await admin.AdminSendAsync(HttpMethod.Post, $"/api/admin/episodes/{episodeId}/uploaded").OkJsonAsync();

        Assert.True(await AdminClient.RunWorkerOnceAsync(api));

        var detail = await admin.GetAsync($"/api/admin/series/{seriesId}", Ct).OkJsonAsync();
        var episode = detail.GetProperty("episodes")[0];
        Assert.Equal("ready", episode.GetProperty("status").GetString());
        Assert.Equal((720, 1280), (episode.GetProperty("width").GetInt32(), episode.GetProperty("height").GetInt32()));
        Assert.Equal(0, detail.GetProperty("publish_problems").GetArrayLength());

        await admin.AdminSendAsync(HttpMethod.Post, $"/api/admin/series/{seriesId}/publish").OkJsonAsync();

        // Viewer in the US: feed, detail, play.
        var viewer = api.CreateClient();
        Task<HttpResponseMessage> Get(string url, string country = "US") =>
            viewer.SendAsync(new HttpRequestMessage(HttpMethod.Get, url).From(country), Ct);

        var feed = await Get("/api/v1/feed").OkJsonAsync();
        var card = feed.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetInt64() == seriesId);
        Assert.Equal("测试工作室", card.GetProperty("author").GetProperty("name").GetString());
        var cover = card.GetProperty("cover").GetString()!;
        Assert.StartsWith("/media/p/posters/", cover);

        var info = await Get($"/api/v1/series/{seriesId}").OkJsonAsync();
        Assert.Equal(1, info.GetProperty("episodes")[0].GetProperty("ep_num").GetInt32());

        var play = await Get($"/api/v1/series/{seriesId}/episodes/1/play").OkJsonAsync();
        var videoUrl = play.GetProperty("video_url").GetString()!;
        Assert.Matches(@"^/media/m/\d+/[A-Za-z0-9_-]+/s/\d+/e/1/v1/master\.m3u8$", videoUrl);

        // Media: master -> variant -> init + segment, all with the one token.
        var master = await (await Get(videoUrl)).Content.ReadAsStringAsync(Ct);
        Assert.StartsWith("#EXTM3U", master);
        Assert.Contains("r0/index.m3u8", master);
        var baseUrl = videoUrl[..(videoUrl.LastIndexOf('/') + 1)];
        var variant = await (await Get(baseUrl + "r0/index.m3u8")).Content.ReadAsStringAsync(Ct);
        Assert.Contains("#EXT-X-MAP:URI=\"init_0.mp4\"", variant);
        Assert.Equal(HttpStatusCode.OK, (await Get(baseUrl + "r0/init_0.mp4")).StatusCode);
        Assert.Contains("seg_0000.m4s", variant);
        var segment = await Get(baseUrl + "r0/seg_0000.m4s");
        Assert.Equal(HttpStatusCode.OK, segment.StatusCode);
        Assert.True((await segment.Content.ReadAsByteArrayAsync(Ct)).Length > 1000);

        // Tampered token, blocked country, public poster.
        var tampered = videoUrl.Replace("/master.m3u8", "/../e/2/v1/master.m3u8");
        Assert.NotEqual(HttpStatusCode.OK, (await Get(tampered)).StatusCode);
        var parts = videoUrl.Split('/');
        parts[4] = parts[4][..^1] + (parts[4][^1] == 'A' ? 'B' : 'A');
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(string.Join('/', parts))).StatusCode);
        Assert.Equal(HttpStatusCode.UnavailableForLegalReasons, (await Get(videoUrl, "CN")).StatusCode);
        var poster = await Get(cover);
        Assert.Equal("image/jpeg", poster.Content.Headers.ContentType?.MediaType);

        // Licensing: restrict to Japan and the US viewer loses access.
        var update = AdminClient.CompleteSeries("霸道总裁的测试", territories: ["JP"]);
        await admin.AdminSendAsync(HttpMethod.Put, $"/api/admin/series/{seriesId}", update).OkJsonAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await Get($"/api/v1/series/{seriesId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get($"/api/v1/series/{seriesId}", "JP")).StatusCode);

        // Takedown: links handed out before it stop working at once, and the CDN edge is told.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.AdminSendAsync(HttpMethod.Post, $"/api/admin/series/{seriesId}/unpublish")).StatusCode);
        var gone = await Get(baseUrl + "r0/seg_0000.m4s", "JP");
        Assert.Equal(HttpStatusCode.Gone, gone.StatusCode);
        Assert.Equal("media_revoked", await gone.ErrorCodeAsync());
        var edge = new HttpRequestMessage(HttpMethod.Get, "/api/edge/config");
        edge.Headers.Add("X-Edge-Secret", "test-edge-secret");
        var edgeConfig = await viewer.SendAsync(edge, Ct).OkJsonAsync();
        Assert.Contains(seriesId, edgeConfig.GetProperty("revoked_series").EnumerateArray().Select(e => e.GetInt64()));
    }

    [Fact]
    public async Task Bulk_import_validates_then_creates_and_is_safe_to_rerun()
    {
        FfmpegLocator.RequireOrSkip();
        await using var api = db.CreateApi();
        var admin = await AdminClient.SignInAsync(api);

        foreach (var name in new[] { "show-a-01.mp4", "show-a-02.mp4" })
        {
            var clip = await FfmpegLocator.MakeClipAsync(Path.Combine(api.StorageRoot, name), 540, 960, 3, audio: name.EndsWith("01.mp4"));
            var ticket = await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/import/incoming", new { file_name = name, content_type = "video/mp4" }).OkJsonAsync();
            await admin.UploadAsync(ticket, clip);
        }

        var licence = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");
        var csv = string.Join('\n',
            "external_id,series_title,category,episode,episode_title,file,tags,author,rights_holder,contract_ref,licence_start,territories,ai_generated,has_real_person",
            $"SRC-A,导入测试剧,ai-duanju,1,第一集,show-a-01.mp4,都市|逆袭,导入工作室,Studio,C-9,{licence},US|JP,是,否",
            $"SRC-A,导入测试剧,ai-duanju,2,第二集,show-a-02.mp4,,,,,,,,");

        var dry = await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/import", new { csv, dry_run = true }).OkJsonAsync();
        Assert.True(dry.GetProperty("ok").GetBoolean(), dry.ToString());
        Assert.Equal("create", dry.GetProperty("series")[0].GetProperty("action").GetString());

        var bad = await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/import",
            new { csv = csv.Replace("show-a-02.mp4", "missing.mp4").Replace("ai-duanju,1", "ai-huanlian,1"), dry_run = true }).OkJsonAsync();
        Assert.False(bad.GetProperty("ok").GetBoolean());
        Assert.Equal(2, bad.GetProperty("errors").GetArrayLength());

        var run = await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/import", new { csv, dry_run = false }).OkJsonAsync();
        Assert.Equal(2, run.GetProperty("queued_jobs").GetInt32());
        Assert.True(await AdminClient.RunWorkerOnceAsync(api));
        Assert.True(await AdminClient.RunWorkerOnceAsync(api));
        Assert.False(await AdminClient.RunWorkerOnceAsync(api));

        var seriesId = run.GetProperty("series")[0].GetProperty("series_id").GetInt64();
        var detail = await admin.GetAsync($"/api/admin/series/{seriesId}", Ct).OkJsonAsync();
        Assert.All(detail.GetProperty("episodes").EnumerateArray(), e => Assert.Equal("ready", e.GetProperty("status").GetString()));
        Assert.Equal(["JP", "US"], detail.GetProperty("territories").EnumerateArray().Select(t => t.GetString()));

        var again = await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/import", new { csv, dry_run = false }).OkJsonAsync();
        Assert.Equal(0, again.GetProperty("queued_jobs").GetInt32());
        Assert.All(again.GetProperty("episodes").EnumerateArray(), e => Assert.Equal("skip_exists", e.GetProperty("action").GetString()));
    }
}
