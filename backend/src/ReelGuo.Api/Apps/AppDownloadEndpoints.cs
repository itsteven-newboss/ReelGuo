using System.Net;
using ReelGuo.Api.Public;
using ReelGuo.Data;

namespace ReelGuo.Api.Apps;

/// <summary>
/// Download links that hand each device its own file. <c>/app/download</c> reads the device from the request (the
/// download page's QR code points here, and it is the link to share); <c>/app/download/{name}</c> is one fixed link per
/// platform. Both always lead to the newest published release in App 版本, so a shared link never goes out of date.
/// When there is nothing to download yet, or the visitor has to choose or act first, they land on the download page
/// with <c>?install=…</c> and the page takes over (web/apps/client/src/pages/AppLanding.tsx).
/// </summary>
public static class AppDownloadEndpoints
{
    /// <summary>Fixed link name → release platform.</summary>
    public static readonly IReadOnlyDictionary<string, string> Links = new Dictionary<string, string>
    {
        ["android"] = "android",
        ["ios"] = "ios",
        ["windows"] = "windows-x86_64",
        ["mac-arm"] = "darwin-aarch64",
        ["mac-intel"] = "darwin-x86_64",
        ["linux"] = "linux-x86_64",
    };

    public static IEndpointRouteBuilder MapAppDownloads(this IEndpointRouteBuilder app)
    {
        string[] methods = [HttpMethods.Get, HttpMethods.Head];
        app.MapMethods("/app/download", methods, ForThisDevice).ExcludeFromDescription();
        app.MapMethods("/app/download/{name}", methods, ForPlatform).ExcludeFromDescription();
        return app;
    }

    private static async Task<IResult> ForThisDevice(HttpContext http, ReelGuoDbContext db, CancellationToken ct)
    {
        NotShared(http);
        var device = DeviceDetect.FromUserAgent(http.Request.Headers.UserAgent);
        // WeChat, QQ and the like block downloads: the page asks to open it in the phone's browser.
        if (device.InApp) return Page("browser");
        return device.Os switch
        {
            DeviceOs.Android => await FileOrPage(db, "android", ct),
            DeviceOs.Windows => await FileOrPage(db, "windows", ct),
            DeviceOs.Linux => await FileOrPage(db, "linux", ct),
            // The profile needs a few taps in Settings afterwards, so the page shows the steps along with the download.
            DeviceOs.Ios => Page("ios"),
            // Apple or Intel chip, or an iPad asking for the desktop site: the browser can tell, the request can't.
            DeviceOs.Mac => Page("mac"),
            _ => Page("auto"),
        };
    }

    private static async Task<IResult> ForPlatform(string name, HttpContext http, ReelGuoDbContext db, WebClipProfile profile, CancellationToken ct)
    {
        NotShared(http);
        if (!Links.ContainsKey(name)) return Results.Redirect("/app/home");
        if (name != "ios") return await FileOrPage(db, name, ct);
        // iPhone: an outside install link when one is published, otherwise the home-screen profile for this address.
        if (await LatestUrl(db, "ios", ct) is { } link) return Results.Redirect(link);
        return Results.Bytes(profile.Build(Origin(http.Request)), WebClipProfile.ContentType);
    }

    private static async Task<IResult> FileOrPage(ReelGuoDbContext db, string name, CancellationToken ct) =>
        await LatestUrl(db, Links[name], ct) is { } url ? Results.Redirect(url) : Page(name);

    private static async Task<string?> LatestUrl(ReelGuoDbContext db, string platform, CancellationToken ct) =>
        (await AppEndpoints.PublishedAsync(db, platform, ct)).FirstOrDefault()?.Url;

    private static IResult Page(string install) => Results.Redirect($"/app/home?install={install}");

    /// <summary>The answer depends on the device and on what is published, so no cache (Cloudflare included) may keep it.</summary>
    private static void NotShared(HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Vary = "User-Agent";
    }

    /// <summary>
    /// The address the visitor used (any of our domains). Behind Cloudflare the server itself speaks plain HTTP, so a
    /// domain name means https; http only for localhost and bare IP addresses (development, LAN tests).
    /// </summary>
    internal static string Origin(HttpRequest request)
    {
        var host = request.Host.Host;
        var direct = host is "localhost" || IPAddress.TryParse(host.Trim('[', ']'), out _);
        return $"{(request.IsHttps || !direct ? "https" : "http")}://{request.Host}";
    }

    /// <summary>A release file uploaded in admin has a site-relative address (/media/…); this makes it a full one.</summary>
    internal static string Absolute(string url, HttpRequest request) =>
        url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal) ? Origin(request) + url : url;
}
