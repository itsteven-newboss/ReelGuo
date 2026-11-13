using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace ReelGuo.Api.Infrastructure;

public sealed class NetworkOptions
{
    public const string Section = "Network";

    /// <summary>
    /// CIDR ranges of the reverse proxies in front of the API (Caddy, a load balancer). Only requests arriving from
    /// these addresses may say who the visitor is (client IP, scheme, CDN country). Empty = trust nobody, which is
    /// right for local development; in containers this is the Docker network.
    /// </summary>
    public List<string> TrustedProxies { get; set; } = [];

    /// <summary>
    /// Header with the visitor's IP. <c>X-Forwarded-For</c> (default) is read right to left, skipping trusted proxies.
    /// Behind Cloudflare use <c>CF-Connecting-IP</c>, and allow only Cloudflare to reach the proxy.
    /// </summary>
    public string ClientIpHeader { get; set; } = "X-Forwarded-For";
}

/// <summary>
/// Real client addresses behind proxies. Rate limits, logs and audit entries use <c>Connection.RemoteIpAddress</c>;
/// without this, every visitor would share the proxy's address (and one rate-limit bucket).
/// </summary>
public static class Network
{
    private const string ViaTrustedProxyKey = "rg.via_trusted_proxy";

    public static IServiceCollection AddReelGuoNetwork(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<NetworkOptions>(configuration.GetSection(NetworkOptions.Section));
        services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<NetworkOptions>>((o, network) =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.ForwardedForHeaderName = network.Value.ClientIpHeader;
            o.ForwardLimit = null;   // walk back through every trusted hop
            // The defaults trust loopback (in all three lists, including the obsolete one); trust only what is configured.
            o.KnownProxies.Clear();
            o.KnownIPNetworks.Clear();
#pragma warning disable ASPDEPR005
            o.KnownNetworks.Clear();
#pragma warning restore ASPDEPR005
            foreach (var cidr in network.Value.TrustedProxies) o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr.Trim()));
        });
        return services;
    }

    /// <summary>Must run first: records whether the request came through a trusted proxy, then applies forwarded headers.</summary>
    public static IApplicationBuilder UseReelGuoNetwork(this IApplicationBuilder app)
    {
        var networks = app.ApplicationServices.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value.KnownIPNetworks.ToList();
        app.Use((context, next) =>
        {
            var peer = context.Connection.RemoteIpAddress;
            if (peer is not null && networks.Any(n => n.Contains(peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer)))
                context.Items[ViaTrustedProxyKey] = true;
            return next(context);
        });
        // With no trusted proxies the forwarded-headers middleware must not run at all: given empty lists it trusts
        // every peer, so anyone could pick their own IP (and rate-limit bucket) with X-Forwarded-For.
        return networks.Count == 0 ? app : app.UseForwardedHeaders();
    }

    /// <summary>True when the request reached the API through a configured proxy (so CDN headers can be believed).</summary>
    public static bool ViaTrustedProxy(HttpContext context) => context.Items.ContainsKey(ViaTrustedProxyKey);

    /// <summary>True when proxies are configured at all; otherwise headers are taken as before (development).</summary>
    public static bool ProxiesConfigured(HttpContext context) =>
        context.RequestServices.GetRequiredService<IOptions<NetworkOptions>>().Value.TrustedProxies.Count > 0;

    /// <summary>
    /// Session cookies are Secure everywhere except development. Behind a TLS-terminating proxy the API itself sees
    /// plain HTTP, so the request scheme can't decide this. (Browsers accept Secure cookies on http://localhost.)
    /// </summary>
    public static bool SecureCookies(HttpContext context) =>
        context.Request.IsHttps || !context.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment();

    /// <summary>The visitor's address as a string for logs and rate-limit partitions.</summary>
    public static string ClientIp(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
