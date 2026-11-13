using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ReelGuo.Api.Users;
using static ReelGuo.Api.Tests.HttpTestExtensions;

namespace ReelGuo.Api.Tests;

/// <summary>Phase 4: per-country age-verification rules.</summary>
public sealed class ComplianceTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string url, string country, string? token = null, object? body = null)
    {
        var request = new HttpRequestMessage(method, url).From(country);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (method != HttpMethod.Get) request.Headers.Add(UserAuth.AppHeader, "1");
        if (body is not null) request.Content = JsonContent.Create(body);
        return client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task Strict_countries_need_a_verified_account_before_any_content()
    {
        await using var api = db.CreateApi();
        var admin = await AdminClient.SignInAsync(api);
        var client = api.CreateClient();

        var rule = await admin.AdminSendAsync(HttpMethod.Put, "/api/admin/geo/age-rules/gb", new { mode = "strict", note = "Online Safety Act 审核 #3" }).OkJsonAsync();
        Assert.Equal("GB", rule.GetProperty("country_code").GetString());

        var config = await Send(client, HttpMethod.Get, "/api/v1/config", "GB").OkJsonAsync();
        Assert.Equal("strict", config.GetProperty("age_check").GetString());
        Assert.Equal("GB", config.GetProperty("country").GetString());
        Assert.Equal("declaration", (await Send(client, HttpMethod.Get, "/api/v1/config", "US").OkJsonAsync()).GetProperty("age_check").GetString());

        var blocked = await Send(client, HttpMethod.Get, "/api/v1/feed", "GB");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("age_verification_required", await blocked.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Get, "/api/v1/feed", "US")).StatusCode);

        // Signing up still works there, but the new account isn't verified yet.
        var name = $"gb{Guid.NewGuid():N}"[..12];
        var auth = await Send(client, HttpMethod.Post, "/api/v1/auth/register", "GB", body: new { username = name, password = "viewer-pass-1", accept_terms = true }).OkJsonAsync();
        var token = auth.GetProperty("access_token").GetString();
        var id = auth.GetProperty("user").GetProperty("id").GetInt64();
        Assert.False((await Send(client, HttpMethod.Get, "/api/v1/me", "GB", token).OkJsonAsync()).GetProperty("user").GetProperty("age_verified").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, HttpMethod.Get, "/api/v1/feed", "GB", token)).StatusCode);

        // The team verifies the member (e.g. after an ID check); content opens for that account only.
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.AdminSendAsync(HttpMethod.Post, $"/api/admin/members/{id}/age-verification", new { verified = true, note = "证件已核验" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Get, "/api/v1/feed", "GB", token)).StatusCode);
        Assert.True((await Send(client, HttpMethod.Get, "/api/v1/me", "GB", token).OkJsonAsync()).GetProperty("user").GetProperty("age_verified").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, HttpMethod.Get, "/api/v1/feed", "GB")).StatusCode);

        // Removing the rule brings back the 18+ confirmation for everyone there.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.AdminSendAsync(HttpMethod.Delete, "/api/admin/geo/age-rules/GB")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Get, "/api/v1/feed", "GB")).StatusCode);

        var audit = await admin.GetAsync("/api/admin/audit-log", Ct).OkJsonAsync();
        var actions = audit.EnumerateArray().Select(a => a.GetProperty("action").GetString()).ToList();
        Assert.Contains("age.rule.set", actions);
        Assert.Contains("age.rule.remove", actions);
        Assert.Contains("member.age_verify", actions);
    }
}
