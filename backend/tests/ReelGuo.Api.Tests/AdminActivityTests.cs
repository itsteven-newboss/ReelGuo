using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ReelGuo.Api.Admin;
using static ReelGuo.Api.Tests.HttpTestExtensions;

namespace ReelGuo.Api.Tests;

/// <summary>Admin work that an update must not cut off: the console's heartbeat and the server-only /health/busy check.</summary>
public sealed class AdminActivityTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    /// <summary>/health/busy as update.ps1 calls it: straight to the API from this machine, or as if through a proxy.</summary>
    private static async Task<(int Status, JsonElement? Body)> Busy(ApiFactory api, bool viaProxy = false)
    {
        var ctx = await api.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/health/busy";
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            if (viaProxy) c.Request.Headers["X-Forwarded-For"] = "203.0.113.9";
        }, Ct);
        if (ctx.Response.StatusCode != 200) return (ctx.Response.StatusCode, null);
        return (200, (await JsonDocument.ParseAsync(ctx.Response.Body, cancellationToken: Ct)).RootElement.Clone());
    }

    [Fact]
    public async Task Optimizing_or_uploading_shows_as_busy_until_it_stops()
    {
        await using var api = db.CreateApi();
        var admin = await AdminClient.SignInAsync(api);

        var idle = await Busy(api);
        Assert.Equal(200, idle.Status);
        Assert.False(idle.Body!.Value.GetProperty("busy").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/activity", new { kind = "optimize", detail = "12/40", active = true })).StatusCode);
        var busy = (await Busy(api)).Body!.Value;
        Assert.True(busy.GetProperty("busy").GetBoolean());
        var item = busy.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(("optimize", "12/40"), (item.GetProperty("kind").GetString(), item.GetProperty("detail").GetString()));
        Assert.StartsWith("admin:", item.GetProperty("admin").GetString());

        // Finished: no longer busy. Unknown kinds and visitors are refused.
        await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/activity", new { kind = "optimize", detail = "40/40", active = false });
        Assert.False((await Busy(api)).Body!.Value.GetProperty("busy").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/activity", new { kind = "nap", active = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/admin/activity") { Content = JsonContent(new { kind = "upload", active = true }) }.WithCsrf(), Ct)).StatusCode);
    }

    [Fact]
    public async Task Busy_check_is_only_for_the_server_itself()
    {
        await using var api = db.CreateApi();
        Assert.Equal(200, (await Busy(api)).Status);
        Assert.Equal(404, (await Busy(api, viaProxy: true)).Status);
        // Anything that came through Caddy or Cloudflare carries one of their headers.
        foreach (var header in new[] { "X-Forwarded-For", "CF-Connecting-IP", "X-Real-IP" })
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/health/busy");
            request.Headers.Add(header, "203.0.113.9");
            Assert.Equal(HttpStatusCode.NotFound, (await api.CreateClient().SendAsync(request, Ct)).StatusCode);
        }
    }

    [Fact]
    public void Reports_expire_when_the_console_stops_sending_them()
    {
        var time = new ManualTime(DateTimeOffset.Parse("2026-11-18T10:00:00Z"));
        var activity = new AdminActivity(time);
        activity.Report("admin:a", "upload", "3/9", active: true);
        Assert.True(activity.Current().Busy);
        time.Advance(AdminActivity.Window + TimeSpan.FromSeconds(1));
        Assert.False(activity.Current().Busy);
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static StringContent JsonContent(object body) =>
        new(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
}
