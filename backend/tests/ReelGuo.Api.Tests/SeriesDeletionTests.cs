using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReelGuo.Api.Users;
using ReelGuo.Data;
using ReelGuo.Data.Entities;
using static ReelGuo.Api.Tests.HttpTestExtensions;

namespace ReelGuo.Api.Tests;

/// <summary>Bulk delete in 内容管理: what goes with a series, what stays, and what other series keep.</summary>
public sealed class SeriesDeletionTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Deleting_series_removes_their_data_and_media_but_keeps_the_ledger()
    {
        FfmpegLocator.RequireOrSkip();
        await using var api = db.CreateApi();
        var admin = await AdminClient.SignInAsync(api);
        var clip = await FfmpegLocator.MakeClipAsync(Path.Combine(api.StorageRoot, "clip.mp4"), 360, 640, 2);

        async Task<long> SeriesWithEpisodeAsync(string title)
        {
            var series = await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/series", AdminClient.CompleteSeries(title)).OkJsonAsync();
            var id = series.GetProperty("id").GetInt64();
            var created = await admin.AdminSendAsync(HttpMethod.Post, $"/api/admin/series/{id}/episodes",
                new { file_name = "第1集.mp4", content_type = "video/mp4", title = "一" }).OkJsonAsync();
            await admin.UploadAsync(created.GetProperty("upload"), clip);
            await admin.AdminSendAsync(HttpMethod.Post, $"/api/admin/episodes/{created.GetProperty("episode").GetProperty("id").GetInt64()}/uploaded").OkJsonAsync();
            Assert.True(await AdminClient.RunWorkerOnceAsync(api));
            return id;
        }
        var doomed = await SeriesWithEpisodeAsync("要删除的剧");
        var draft = await SeriesWithEpisodeAsync("也要删除的草稿");
        var kept = await SeriesWithEpisodeAsync("保留的剧");
        await admin.AdminSendAsync(HttpMethod.Post, $"/api/admin/series/{doomed}/publish").OkJsonAsync();
        await admin.AdminSendAsync(HttpMethod.Post, $"/api/admin/series/{kept}/publish").OkJsonAsync();

        // A viewer who bought, liked and commented on the published one, and on the kept one too.
        var register = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/register")
        {
            Content = JsonContent.Create(new { username = $"d{Guid.NewGuid():N}"[..14], password = "viewer-pass-1", accept_terms = true }),
        };
        register.Headers.Add(UserAuth.AppHeader, "1");
        var userId = (await api.CreateClient().SendAsync(register, Ct).OkJsonAsync()).GetProperty("user").GetProperty("id").GetInt64();
        using (var scope = api.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ReelGuoDbContext>();
            var now = DateTimeOffset.UtcNow;
            foreach (var id in new[] { doomed, kept })
            {
                ctx.EpisodeUnlocks.Add(new EpisodeUnlock { UserId = userId, SeriesId = id, EpisodeNumber = 1, Coins = 10, At = now });
                ctx.Add(new SeriesLike { UserId = userId, SeriesId = id, CreatedAt = now });
                ctx.Comments.Add(new Comment { TargetType = CommentTarget.Series, TargetId = id, EpisodeNumber = 1, UserId = userId, Text = "好看", Country = "US", Status = CommentStatus.Approved, CreatedAt = now });
                ctx.Add(new WalletEntry { UserId = userId, Kind = WalletEntryKind.Unlock, CoinsDelta = -10, SeriesId = id, EpisodeNumber = 1, Description = "解锁", At = now });
            }
            await ctx.SaveChangesAsync(Ct);
        }
        var mediaRoot = Path.Combine(api.StorageRoot, "media");
        var rawRoot = Path.Combine(api.StorageRoot, "raw");
        Assert.True(Directory.Exists(Path.Combine(mediaRoot, "s", doomed.ToString())));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(mediaRoot, "p", "posters"), $"{doomed}-*"));
        Assert.True(Directory.Exists(Path.Combine(rawRoot, "uploads", doomed.ToString())));

        // The confirmation shows what goes with each series; unknown ids are skipped.
        var preview = await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/series/delete-preview", new { ids = new[] { doomed, draft, 987654321L } }).OkJsonAsync();
        var rows = preview.EnumerateArray().ToList();
        Assert.Equal([doomed, draft], rows.Select(r => r.GetProperty("id").GetInt64()));
        Assert.Equal(("published", 1, 1, 1, 0), (rows[0].GetProperty("status").GetString(), rows[0].GetProperty("episodes").GetInt32(),
            rows[0].GetProperty("buyers").GetInt32(), rows[0].GetProperty("comments").GetInt32(), rows[0].GetProperty("cos_files").GetInt32()));
        Assert.Equal(("draft", 0), (rows[1].GetProperty("status").GetString(), rows[1].GetProperty("buyers").GetInt32()));

        var result = await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/series/delete", new { ids = new[] { doomed, draft, 987654321L } }).OkJsonAsync();
        Assert.Equal(2, result.GetProperty("deleted").GetInt32());

        // Gone for admins and viewers, with everything that pointed at them.
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/admin/series/{doomed}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/series/{doomed}").From("US"), Ct)).StatusCode);
        using (var scope = api.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ReelGuoDbContext>();
            long[] gone = [doomed, draft];
            Assert.Equal(0, await ctx.Episodes.CountAsync(e => gone.Contains(e.SeriesId), Ct));
            Assert.Equal(0, await ctx.EpisodeUnlocks.CountAsync(u => gone.Contains(u.SeriesId), Ct));
            Assert.Equal(0, await ctx.Set<SeriesLike>().CountAsync(l => gone.Contains(l.SeriesId), Ct));
            Assert.Equal(0, await ctx.Comments.CountAsync(c => c.TargetType == CommentTarget.Series && gone.Contains(c.TargetId), Ct));
            // The ledger is the record of what was paid: it stays.
            Assert.Equal(1, await ctx.Set<WalletEntry>().CountAsync(w => w.SeriesId == doomed, Ct));
            Assert.Equal(2, await ctx.AuditLog.CountAsync(a => a.Action == "series.delete", Ct));

            // The other series is untouched.
            Assert.Equal(1, await ctx.Episodes.CountAsync(e => e.SeriesId == kept, Ct));
            Assert.Equal(1, await ctx.EpisodeUnlocks.CountAsync(u => u.SeriesId == kept, Ct));
            Assert.Equal(1, await ctx.Comments.CountAsync(c => c.TargetType == CommentTarget.Series && c.TargetId == kept, Ct));
        }
        Assert.False(Directory.Exists(Path.Combine(mediaRoot, "s", doomed.ToString())));
        Assert.False(Directory.Exists(Path.Combine(mediaRoot, "s", draft.ToString())));
        Assert.Empty(Directory.GetFiles(Path.Combine(mediaRoot, "p", "posters"), $"{doomed}-*"));
        Assert.False(Directory.Exists(Path.Combine(rawRoot, "uploads", doomed.ToString())));
        Assert.True(Directory.Exists(Path.Combine(mediaRoot, "s", kept.ToString())));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(mediaRoot, "p", "posters"), $"{kept}-*"));
        Assert.True(Directory.Exists(Path.Combine(rawRoot, "uploads", kept.ToString())));

        // Batch limits.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/series/delete", new { ids = Array.Empty<long>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/series/delete",
            new { ids = Enumerable.Range(1, 101).Select(i => (long)i).ToArray() })).StatusCode);
    }
}
