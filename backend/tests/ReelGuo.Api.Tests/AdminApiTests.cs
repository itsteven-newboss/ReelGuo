using System.Net;
using System.Net.Http.Json;
using ReelGuo.Data.Geo;
using static ReelGuo.Api.Tests.HttpTestExtensions;

namespace ReelGuo.Api.Tests;

public sealed class AdminApiTests
{
    [Fact]
    public async Task Admin_api_requires_sign_in()
    {
        await using var api = new ApiFactory(store: new FakeBlockedCountryStore());
        var response = await api.CreateClient().GetAsync("/api/admin/session", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthorized", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task State_changing_requests_need_the_csrf_header()
    {
        await using var api = new ApiFactory(store: new FakeBlockedCountryStore());
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/admin/geo/blocked-countries/FR") { Content = JsonContent.Create(new { note = "x" }) };
        request.Headers.Add(TestAdminHandler.Header, "tester"); // signed in, but no CSRF header

        var response = await api.CreateClient().SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("csrf_header_missing", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task Login_requires_the_csrf_header_too()
    {
        await using var api = new ApiFactory(store: new FakeBlockedCountryStore());
        var response = await api.CreateClient().PostAsJsonAsync("/api/admin/auth/login", new { username = "a", password = "b" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Session_reports_the_signed_in_admin()
    {
        await using var api = new ApiFactory(store: new FakeBlockedCountryStore());
        var response = await api.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/admin/session").AsAdmin("zhangsan"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("admin:zhangsan", (await response.JsonAsync()).GetProperty("actor").GetString());
    }

    [Theory]
    [InlineData("ZZ")]
    [InlineData("CHN")]
    [InlineData("1")]
    public async Task Blocking_an_invalid_code_is_rejected(string code)
    {
        await using var api = new ApiFactory(store: new FakeBlockedCountryStore());
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/geo/blocked-countries/{code}")
        {
            Content = JsonContent.Create(new { note = "test" }),
        }.AsAdmin();
        var response = await api.CreateClient().SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_country_code", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task Country_code_list_has_every_iso_code_plus_the_special_codes()
    {
        await using var api = new ApiFactory(store: new FakeBlockedCountryStore());
        var response = await api.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/admin/geo/country-codes").AsAdmin(), Ct);
        var codes = (await response.JsonAsync()).EnumerateArray().Select(e => e.GetString()!).ToList();

        Assert.Equal(249 + 3, codes.Count);
        Assert.Contains("CN", codes);
        Assert.Contains("XX", codes);
        Assert.Contains("T1", codes);
        Assert.Contains("XK", codes);
    }

    [Fact]
    public async Task Malformed_query_values_are_400_not_500()
    {
        await using var api = new ApiFactory(store: new FakeBlockedCountryStore());
        var response = await api.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/admin/series?page=abc").AsAdmin(), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("bad_request", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task Edge_config_requires_the_edge_secret()
    {
        await using var api = new ApiFactory(store: new FakeBlockedCountryStore("CN", "XX"));
        var client = api.CreateClient();

        var anonymous = await client.GetAsync("/api/edge/config", Ct);
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/edge/config");
        request.Headers.Add("X-Edge-Secret", "test-edge-secret");
        var authorized = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        var body = await authorized.JsonAsync();
        Assert.Equal(["CN", "XX"], body.GetProperty("blocked_countries").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(0, body.GetProperty("revoked_series").GetArrayLength());
    }
}

public sealed class CountryCodesTests
{
    [Fact]
    public void Iso_list_has_all_249_assigned_codes() => Assert.Equal(249, CountryCodes.Iso3166.Count);

    [Theory]
    [InlineData("cn", "CN")]
    [InlineData(" us ", "US")]
    [InlineData("xx", "XX")]
    [InlineData("t1", "T1")]
    [InlineData("ZZ", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Normalize(string? input, string? expected) => Assert.Equal(expected, CountryCodes.Normalize(input));
}
