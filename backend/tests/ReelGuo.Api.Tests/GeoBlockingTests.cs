using System.Net;
using static ReelGuo.Api.Tests.HttpTestExtensions;

namespace ReelGuo.Api.Tests;

public sealed class GeoBlockingTests
{
    private static HttpRequestMessage Get(string url) => new(HttpMethod.Get, url);

    [Theory]
    [InlineData("CN")]
    [InlineData("cn")]
    [InlineData(" CN ")]
    public async Task Blocked_country_gets_451_on_the_public_api(string country)
    {
        await using var api = new ApiFactory(store: new FakeBlockedCountryStore("CN"));
        var response = await api.CreateClient().SendAsync(Get("/api/v1/config").From(country), Ct);

        Assert.Equal(HttpStatusCode.UnavailableForLegalReasons, response.StatusCode);
        Assert.Equal("region_blocked", await response.ErrorCodeAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Allowed_country_reaches_the_api()
    {
        await using var api = new ApiFactory(store: new FakeBlockedCountryStore("CN"));
        var response = await api.CreateClient().SendAsync(Get("/api/v1/config").From("US"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Equal("ReelGuo", body.GetProperty("app_name").GetString());
        Assert.Equal("zh-CN", body.GetProperty("default_locale").GetString());
    }

    [Fact]
    public async Task Health_checks_and_admin_api_are_never_blocked()
    {
        await using var api = new ApiFactory(store: new FakeBlockedCountryStore("CN"));
        var client = api.CreateClient();

        var health = await client.SendAsync(Get("/health/live").From("CN"), Ct);
        var admin = await client.SendAsync(Get("/api/admin/session").From("CN").AsAdmin(), Ct);

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
    }

    [Fact]
    public async Task Debug_header_is_ignored_outside_development()
    {
        await using var api = new ApiFactory(
            environment: "Production",
            settings: new Dictionary<string, string?> { ["Geo:AllowDebugHeader"] = "true" },
            store: new FakeBlockedCountryStore("CN"));
        var response = await api.CreateClient().SendAsync(Get("/api/v1/config").From("CN"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Cdn_country_header_is_used_only_when_trusted()
    {
        var blocked = new FakeBlockedCountryStore("CN");
        var trusted = new Dictionary<string, string?> { ["Geo:TrustCountryHeader"] = "true" };

        await using var untrustedApi = new ApiFactory(store: blocked);
        await using var trustedApi = new ApiFactory(settings: trusted, store: blocked);

        HttpRequestMessage Request()
        {
            var r = Get("/api/v1/config");
            r.Headers.Add("CF-IPCountry", "CN");
            return r;
        }

        Assert.Equal(HttpStatusCode.OK, (await untrustedApi.CreateClient().SendAsync(Request(), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.UnavailableForLegalReasons, (await trustedApi.CreateClient().SendAsync(Request(), Ct)).StatusCode);
    }

    [Fact]
    public async Task Unknown_country_is_blocked_only_when_XX_is_listed()
    {
        await using var open = new ApiFactory(store: new FakeBlockedCountryStore("CN"));
        await using var strict = new ApiFactory(store: new FakeBlockedCountryStore("CN", "XX"));

        Assert.Equal(HttpStatusCode.OK, (await open.CreateClient().GetAsync("/api/v1/config", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.UnavailableForLegalReasons, (await strict.CreateClient().GetAsync("/api/v1/config", Ct)).StatusCode);
    }

    [Fact]
    public async Task Blocking_can_be_switched_off()
    {
        await using var api = new ApiFactory(
            settings: new Dictionary<string, string?> { ["Geo:Enabled"] = "false" },
            store: new FakeBlockedCountryStore("CN"));
        var response = await api.CreateClient().SendAsync(Get("/api/v1/config").From("CN"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
