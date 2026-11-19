using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReelGuo.Data;
using ReelGuo.Data.Entities;
using static ReelGuo.Api.Tests.HttpTestExtensions;

namespace ReelGuo.Api.Tests;

/// <summary>Card badges set in admin: 原创 and the status badge (新片 by default for new series).</summary>
public sealed class CardBadgeTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    [Fact]
    public void New_series_start_as_original_with_the_new_badge()
    {
        // Folder upload and batch import create series this way, without a badge of their own.
        var series = new Series { Title = "上传的剧" };
        Assert.True(series.IsOriginal);
        Assert.Equal(CardBadge.New, series.CardBadge);
    }

    [Fact]
    public async Task Admin_sets_the_badges_one_by_one_or_in_bulk_and_cards_carry_them()
    {
        await using var api = db.CreateApi();
        var admin = await AdminClient.SignInAsync(api);
        async Task<JsonElement> Series(long id) => await admin.AdminSendAsync(HttpMethod.Get, $"/api/admin/series/{id}").OkJsonAsync();
        async Task<long> Create(string title) => (await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/series", AdminClient.CompleteSeries(title)).OkJsonAsync()).GetProperty("id").GetInt64();

        var a = await Create("角标甲");
        var b = await Create("角标乙");
        var created = await Series(a);
        Assert.True(created.GetProperty("is_original").GetBoolean());
        Assert.Equal("new", created.GetProperty("card_badge").GetString());

        // The editor sets the badge; saving without one keeps it.
        var edit = JsonSerializer.SerializeToNode(AdminClient.CompleteSeries("角标甲"))!.AsObject();
        edit["card_badge"] = "hot";
        await admin.AdminSendAsync(HttpMethod.Put, $"/api/admin/series/{a}", edit).OkJsonAsync();
        Assert.Equal("hot", (await Series(a)).GetProperty("card_badge").GetString());
        await admin.AdminSendAsync(HttpMethod.Put, $"/api/admin/series/{a}", AdminClient.CompleteSeries("角标甲")).OkJsonAsync();
        Assert.Equal("hot", (await Series(a)).GetProperty("card_badge").GetString());

        // In bulk: only the fields given change.
        var result = await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/series/labels", new { ids = new[] { a, b, 987654321L }, card_badge = "none" }).OkJsonAsync();
        Assert.Equal(2, result.GetProperty("updated").GetInt32());
        Assert.Equal(("none", true), ((await Series(a)).GetProperty("card_badge").GetString(), (await Series(a)).GetProperty("is_original").GetBoolean()));
        await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/series/labels", new { ids = new[] { b }, is_original = false }).OkJsonAsync();
        var bAfter = await Series(b);
        Assert.Equal(("none", false), (bAfter.GetProperty("card_badge").GetString(), bAfter.GetProperty("is_original").GetBoolean()));

        var list = await admin.AdminSendAsync(HttpMethod.Get, "/api/admin/series?q=角标").OkJsonAsync();
        Assert.Contains(list.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetInt64() == b && !i.GetProperty("is_original").GetBoolean() && i.GetProperty("card_badge").GetString() == "none");

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/series/labels", new { ids = Array.Empty<long>(), card_badge = "new" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/series/labels", new { ids = new[] { a } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/series/labels", new { ids = Enumerable.Range(1, 101).Select(i => (long)i), card_badge = "new" })).StatusCode);

        // Viewers' cards carry the badge.
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ReelGuoDbContext>();
            await ctx.Series.Where(s => s.Id == a).ExecuteUpdateAsync(u => u
                .SetProperty(s => s.Status, SeriesStatus.Published).SetProperty(s => s.PublishedAt, DateTimeOffset.UtcNow)
                .SetProperty(s => s.EpisodeCount, 1).SetProperty(s => s.CardBadge, CardBadge.New), Ct);
        }
        var feed = await api.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/v1/feed?sort=latest&size=50").From("US"), Ct).OkJsonAsync();
        var card = feed.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetInt64() == a);
        Assert.Equal(("new", true), (card.GetProperty("card_badge").GetString(), card.GetProperty("is_original").GetBoolean()));
    }
}
