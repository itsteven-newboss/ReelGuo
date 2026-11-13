using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReelGuo.Data;
using ReelGuo.Data.Entities;
using ReelGuo.Worker;
using static ReelGuo.Api.Tests.HttpTestExtensions;

namespace ReelGuo.Api.Tests;

/// <summary>Phase 4: worker heartbeats, the status page and playback-quality samples.</summary>
public sealed class OperationsTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Worker_health_follows_heartbeats_and_the_status_page_reports_them()
    {
        await using var api = db.CreateApi();
        var client = api.CreateClient();
        var admin = await AdminClient.SignInAsync(api);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/worker", Ct)).StatusCode);
        var before = await admin.GetAsync("/api/admin/system/status", Ct).OkJsonAsync();
        Assert.Contains(before.GetProperty("alerts").EnumerateArray(), a => a.GetProperty("level").GetString() == "critical"
            && a.GetProperty("message").GetString()!.Contains("Worker"));

        var stats = new WorkerStats();
        stats.Started(7);
        stats.Finished(succeeded: true);
        await new HeartbeatService(api.Services.GetRequiredService<IServiceScopeFactory>(), stats, TimeProvider.System, NullLogger<HeartbeatService>.Instance)
            .BeatAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/worker", Ct)).StatusCode);
        var status = await admin.GetAsync("/api/admin/system/status", Ct).OkJsonAsync();
        Assert.True(status.GetProperty("database_ok").GetBoolean());
        Assert.True(status.GetProperty("storage_ok").GetBoolean());
        var worker = Assert.Single(status.GetProperty("workers").EnumerateArray());
        Assert.True(worker.GetProperty("alive").GetBoolean());
        Assert.Equal(1, worker.GetProperty("jobs_succeeded").GetInt64());
        Assert.DoesNotContain(status.GetProperty("alerts").EnumerateArray(), a => a.GetProperty("message").GetString()!.Contains("Worker："));
    }

    [Fact]
    public async Task Playback_samples_are_stored_sanitised_and_counted()
    {
        await using var api = db.CreateApi();
        var admin = await AdminClient.SignInAsync(api);
        long seriesId;
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ReelGuoDbContext>();
            var now = DateTimeOffset.UtcNow;
            var series = new Series
            {
                Title = "播放质量测试", CategoryId = 1, Status = SeriesStatus.Published, PublishedAt = now, Territories = ["*"],
                LicenceStart = DateOnly.FromDateTime(now.UtcDateTime.AddDays(-1)), RightsHolder = "T", ContractRef = "C",
                AiGenerated = true, HasRealPerson = false, CreatedAt = now, UpdatedAt = now,
            };
            ctx.Series.Add(series);
            await ctx.SaveChangesAsync(Ct);
            seriesId = series.Id;
        }

        var body = new
        {
            session_id = "qoe-session-0001",
            events = new[] { new { type = "start", series_id = seriesId, ep = 1 } },
            samples = new object[]
            {
                new { series_id = seriesId, ep = 1, startup_ms = 850, rebuffer_count = 1, rebuffer_ms = 1200, watched_s = 42.5, max_height = 720, error = (string?)null, device = "mobile", browser = "safari" },
                new { series_id = seriesId, ep = 1, startup_ms = (int?)null, rebuffer_count = 0, rebuffer_ms = 0, watched_s = 0.0, max_height = (int?)null, error = "manifestLoadError", device = "desktop", browser = "netscape" },
                new { series_id = seriesId, ep = 1, startup_ms = 999_999, rebuffer_count = 0, rebuffer_ms = 0, watched_s = 1.0, max_height = 99_999, error = "<script>", device = "desktop", browser = "chrome" },
                new { series_id = 999_999_999L, ep = 1, startup_ms = 100, rebuffer_count = 0, rebuffer_ms = 0, watched_s = 1.0, max_height = 360, error = (string?)null, device = "mobile", browser = "chrome" },
            },
        };
        var res = await api.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/v1/events") { Content = JsonContent.Create(body) }.From("US"), Ct);
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        await using (var scope = api.Services.CreateAsyncScope())
        {
            var rows = await scope.ServiceProvider.GetRequiredService<ReelGuoDbContext>().PlaybackSamples.Where(s => s.SeriesId == seriesId).OrderBy(s => s.Id).ToListAsync(Ct);
            Assert.Equal(3, rows.Count);   // the unknown series is dropped
            Assert.Equal(850, rows[0].StartupMs);
            Assert.Equal("safari", rows[0].Browser);
            Assert.Equal("other", rows[1].Browser);
            Assert.Equal("manifestLoadError", rows[1].Error);
            Assert.Equal(120_000, rows[2].StartupMs);   // clamped
            Assert.Equal(4320, rows[2].MaxHeight);
            Assert.Null(rows[2].Error);                 // not a code: dropped
        }
        var status = await admin.GetAsync("/api/admin/system/status", Ct).OkJsonAsync();
        Assert.Equal(1, status.GetProperty("playback_errors_last_hour").GetInt32());
        Assert.Equal(1, status.GetProperty("plays_last_hour").GetInt32());
        var overview = await admin.GetAsync("/api/admin/analytics/overview?days=7", Ct).OkJsonAsync();
        Assert.Equal(7, overview.GetProperty("daily").GetArrayLength());
        Assert.Equal(1, overview.GetProperty("plays").GetInt32());
        var quality = overview.GetProperty("quality");
        Assert.Equal(3, quality.GetProperty("samples").GetInt32());
        Assert.Equal(1.0 / 3, quality.GetProperty("error_rate").GetDouble(), 3);
        var today = overview.GetProperty("daily")[6];
        Assert.Equal(1, today.GetProperty("plays").GetInt32());
        Assert.Equal((850 + 120_000) / 2.0, today.GetProperty("quality").GetProperty("startup_p50_ms").GetDouble());   // median of the two that started
        Assert.Contains(overview.GetProperty("by_browser").EnumerateArray(), b => b.GetProperty("key").GetString() == "safari");
        Assert.Equal(seriesId, overview.GetProperty("top_series")[0].GetProperty("series_id").GetInt64());
    }
}
