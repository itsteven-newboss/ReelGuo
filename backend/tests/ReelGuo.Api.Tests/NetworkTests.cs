using System.Net;
using ReelGuo.Api.Users;
using static ReelGuo.Api.Tests.HttpTestExtensions;

namespace ReelGuo.Api.Tests;

/// <summary>Client addresses and CDN headers behind reverse proxies (Phase 4).</summary>
public sealed class NetworkTests
{
    private static ApiFactory Api(params string[] trustedProxies)
    {
        var settings = new Dictionary<string, string?> { ["Geo:TrustCountryHeader"] = "true", ["Geo:AllowDebugHeader"] = "false" };
        for (var i = 0; i < trustedProxies.Length; i++) settings[$"Network:TrustedProxies:{i}"] = trustedProxies[i];
        return new ApiFactory(settings: settings, store: new FakeBlockedCountryStore("FR"));
    }

    /// <summary>Refresh without a cookie answers 401 without touching the database, and is limited to 60 per minute per IP.</summary>
    private static Task<HttpResponseMessage> Refresh(HttpClient client, string clientIp)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        request.Headers.Add(UserAuth.AppHeader, "1");
        request.Headers.Add("X-Forwarded-For", clientIp);
        return client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task Rate_limits_count_each_visitor_behind_a_trusted_proxy_separately()
    {
        await using var api = Api("127.0.0.0/8");
        var client = api.CreateClient();
        for (var i = 0; i < 60; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(client, "203.0.113.7")).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await Refresh(client, "203.0.113.7")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(client, "198.51.100.20")).StatusCode);
    }

    [Fact]
    public async Task Forwarded_addresses_from_untrusted_peers_are_ignored()
    {
        await using var api = Api();   // no trusted proxies: X-Forwarded-For can be forged, so everyone is the peer
        var client = api.CreateClient();
        for (var i = 0; i < 60; i++) await Refresh(client, $"203.0.113.{i}");

        Assert.Equal(HttpStatusCode.TooManyRequests, (await Refresh(client, "198.51.100.20")).StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.0/8", HttpStatusCode.UnavailableForLegalReasons)]   // came through the proxy: header believed
    [InlineData("10.0.0.0/8", HttpStatusCode.OK)]                             // didn't: a forged header is ignored (XX)
    public async Task Cdn_country_header_is_believed_only_through_a_trusted_proxy(string proxies, HttpStatusCode expected)
    {
        await using var api = Api(proxies);
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/config");
        request.Headers.Add("CF-IPCountry", "FR");
        Assert.Equal(expected, (await api.CreateClient().SendAsync(request, Ct)).StatusCode);
    }
}
