using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ReelGuo.Api.Apps;
using static ReelGuo.Api.Tests.HttpTestExtensions;

namespace ReelGuo.Api.Tests;

/// <summary>Download links that hand each device its own file, the iPhone install profile, and installers uploaded in admin.</summary>
public sealed class AppDownloadTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    private const string AndroidChrome = "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Mobile Safari/537.36";
    private const string IphoneSafari = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Mobile/15E148 Safari/604.1";
    private const string WindowsEdge = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0";
    private const string MacSafari = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Safari/605.1.15";
    private const string WeChatAndroid = "Mozilla/5.0 (Linux; Android 13; V2148A Build/TP1A.220624.014; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/116.0.0.0 Mobile Safari/537.36 XWEB/1160065 MMWEBSDK/20231202 MMWEBID/2247 MicroMessenger/8.0.47.2560(0x28002F30) WeChat/arm64 Weixin NetType/WIFI Language/zh_CN ABI/arm64";
    private const string QqAppIphone = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 QQ/9.0.0.611 V1_IPH_SQ_9.0.0_1_APP_A Core/WKWebView Device/Apple(iPhone 12) NetType/WIFI";
    private const string QqBrowser = "Mozilla/5.0 (Linux; U; Android 12; zh-cn; PDEM10 Build/SKQ1.211019.001) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/109.0.5414.86 MQQBrowser/14.4 Mobile Safari/537.36";
    private const string HarmonyNext = "Mozilla/5.0 (Phone; OpenHarmony 5.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/114.0.0.0 Safari/537.36 ArkWeb/4.1.6.1 Mobile HuaweiBrowser/5.0.4.300";
    private const string Harmony4 = "Mozilla/5.0 (Linux; Android 12; HarmonyOS; NOH-AN00; HMSCore 6.13.0.302) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/114.0.5735.196 HuaweiBrowser/14.0.5.302 Mobile Safari/537.36";
    private const string LinuxFirefox = "Mozilla/5.0 (X11; Linux x86_64; rv:132.0) Gecko/20100101 Firefox/132.0";
    private const string ChromeOs = "Mozilla/5.0 (X11; CrOS x86_64 14541.0.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    // The first bytes of each kind of file, enough for the type check.
    private static readonly byte[] Apk = [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x08, 0x00, 1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly byte[] Exe = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 9, 9, 9, 9];

    [Theory]
    [InlineData(AndroidChrome, DeviceOs.Android, false)]
    [InlineData(IphoneSafari, DeviceOs.Ios, false)]
    [InlineData(WindowsEdge, DeviceOs.Windows, false)]
    [InlineData(MacSafari, DeviceOs.Mac, false)]
    [InlineData(WeChatAndroid, DeviceOs.Android, true)]
    [InlineData(QqAppIphone, DeviceOs.Ios, true)]
    [InlineData(QqBrowser, DeviceOs.Android, false)]       // QQ浏览器 is a real browser, unlike the QQ app
    [InlineData(HarmonyNext, DeviceOs.Harmony, false)]     // HarmonyOS NEXT can't install APKs
    [InlineData(Harmony4, DeviceOs.Android, false)]        // older HarmonyOS can
    [InlineData(LinuxFirefox, DeviceOs.Linux, false)]
    [InlineData(ChromeOs, DeviceOs.Other, false)]
    [InlineData("", DeviceOs.Other, false)]
    public void Devices_are_read_from_the_user_agent(string userAgent, DeviceOs os, bool inApp) =>
        Assert.Equal(new Device(os, inApp), DeviceDetect.FromUserAgent(userAgent));

    private static HttpRequestMessage Request(string url, string? userAgent = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url).From("US");
        if (userAgent is not null) request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        return request;
    }

    private static Task<HttpResponseMessage> Upload(HttpClient client, string platform, string name, byte[] bytes) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/admin/app-releases/files?platform={platform}&name={Uri.EscapeDataString(name)}")
            { Content = new ByteArrayContent(bytes) }.WithCsrf(), Ct);

    private static Task<JsonElement> Publish(HttpClient admin, string platform, string url, string? signature = null) =>
        admin.AdminSendAsync(HttpMethod.Post, "/api/admin/app-releases", new
        {
            platform, version = "0.6.0", url, signature, sha256 = (string?)null, notes = "", mandatory = false, published = true,
        }).OkJsonAsync();

    [Fact]
    public async Task Each_device_gets_its_own_file_and_the_rest_land_on_the_download_page()
    {
        await using var api = db.CreateApi();
        var admin = await AdminClient.SignInAsync(api);
        var visitor = api.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        async Task<string> Location(string path, string userAgent)
        {
            var r = await visitor.SendAsync(Request(path, userAgent), Ct);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
            return r.Headers.Location!.OriginalString;
        }

        // Nothing published yet: the download page says so.
        Assert.Equal("/app/home?install=android", await Location("/app/download", AndroidChrome));
        Assert.Equal("/app/home?install=windows", await Location("/app/download", WindowsEdge));

        // Installers uploaded in admin: stored under /media, with their SHA-256 worked out.
        var apk = await Upload(admin, "android", "ReelGuo_0.6.0.apk", Apk).OkJsonAsync();
        var apkUrl = apk.GetProperty("url").GetString()!;
        Assert.StartsWith("/media/p/app/android/", apkUrl);
        Assert.EndsWith("/ReelGuo_0.6.0.apk", apkUrl);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Apk)), apk.GetProperty("sha256").GetString());
        Assert.Equal(Apk.Length, apk.GetProperty("size").GetInt64());
        var exeUrl = (await Upload(admin, "windows-x86_64", "ReelGuo_0.6.0_x64-setup.exe", Exe).OkJsonAsync()).GetProperty("url").GetString()!;
        var androidRelease = (await Publish(admin, "android", apkUrl)).GetProperty("id").GetInt32();
        await Publish(admin, "windows-x86_64", exeUrl, signature: "c2ln");

        Assert.Equal(apkUrl, await Location("/app/download", AndroidChrome));
        Assert.Equal(exeUrl, await Location("/app/download", WindowsEdge));
        Assert.Equal("/app/home?install=ios", await Location("/app/download", IphoneSafari));
        Assert.Equal("/app/home?install=mac", await Location("/app/download", MacSafari));
        Assert.Equal("/app/home?install=browser", await Location("/app/download", WeChatAndroid));
        Assert.Equal("/app/home?install=browser", await Location("/app/download", QqAppIphone));
        Assert.Equal("/app/home?install=auto", await Location("/app/download", HarmonyNext));
        Assert.Equal(apkUrl, await Location("/app/download", Harmony4));
        // The fixed per-platform links don't look at the device.
        Assert.Equal(apkUrl, await Location("/app/download/android", WindowsEdge));
        Assert.Equal(exeUrl, await Location("/app/download/windows", AndroidChrome));
        Assert.Equal("/app/home?install=mac-arm", await Location("/app/download/mac-arm", MacSafari));
        Assert.Equal("/app/home", await Location("/app/download/symbian", AndroidChrome));

        // Android installs only what is served as an APK.
        var file = await visitor.SendAsync(Request(apkUrl, AndroidChrome), Ct);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("application/vnd.android.package-archive", file.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Apk, await file.Content.ReadAsByteArrayAsync(Ct));

        // The apps open the file outside the web view, so they get its full address on the server they asked.
        var boot = await visitor.SendAsync(Request("/api/v1/app/bootstrap?platform=android&version=0.5.0"), Ct).OkJsonAsync();
        Assert.Equal($"http://localhost{apkUrl}", boot.GetProperty("latest").GetProperty("url").GetString());
        // The download page keeps it relative, so it downloads from whichever domain the visitor is on.
        var listed = await visitor.SendAsync(Request("/api/v1/app/releases"), Ct).OkJsonAsync();
        Assert.Contains(listed.EnumerateArray(), r => r.GetProperty("url").GetString() == apkUrl);

        // Deleting the release deletes its file.
        var stored = Path.Combine(api.StorageRoot, "media", apkUrl["/media/".Length..].Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(stored));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.AdminSendAsync(HttpMethod.Delete, $"/api/admin/app-releases/{androidRelease}")).StatusCode);
        Assert.False(File.Exists(stored));
        Assert.Equal("/app/home?install=android", await Location("/app/download", AndroidChrome));

        // Wrong files, iPhone uploads and visitors are refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(admin, "android", "fake.apk", Exe)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(admin, "android", "ReelGuo.exe", Exe)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(admin, "darwin-aarch64", "ReelGuo.dmg", Exe)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(admin, "ios", "ReelGuo.ipa", Apk)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(admin, "android", "empty.apk", [])).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Upload(api.CreateClient(), "android", "ReelGuo.apk", Apk)).StatusCode);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(api.StorageRoot, "media", "p", "app", "android"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Iphones_get_a_home_screen_profile_for_the_address_they_used_unless_an_install_link_is_published()
    {
        await using var api = db.CreateApi();
        var visitor = api.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        async Task<(HttpResponseMessage Response, string Text)> Profile(string host)
        {
            var r = await visitor.SendAsync(Request($"http://{host}/app/download/ios", IphoneSafari), Ct);
            return (r, Encoding.UTF8.GetString(await r.Content.ReadAsByteArrayAsync(Ct)));
        }

        var (response, plist) = await Profile("m.example.net");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-apple-aspen-config", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.StartsWith("<?xml", plist);
        Assert.Contains("<string>com.apple.webClip.managed</string>", plist);
        Assert.Contains("<string>https://m.example.net/</string>", plist);   // a domain is reached over https (Cloudflare)
        Assert.Contains($"<string>{SiteBrand.Default.Name}</string>", plist);
        var icon = Convert.FromBase64String(plist.Split("<data>")[1].Split("</data>")[0]);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], icon[..4]);

        // The same address gives the same profile; another address replaces it (same identifier) rather than adding one.
        Assert.Equal(plist, (await Profile("m.example.net")).Text);
        var other = (await Profile("m2.example.org")).Text;
        Assert.Contains("<string>https://m2.example.org/</string>", other);
        Assert.Contains($"<string>{WebClipProfile.IdentifierFor(SiteBrand.Default)}</string>", other);
        Assert.NotEqual(Uuids(plist), Uuids(other));
        Assert.Contains("<string>http://localhost/</string>", (await Profile("localhost")).Text);

        // An install link from a signing service, once published, is offered instead.
        var admin = await AdminClient.SignInAsync(api);
        var link = (await Publish(admin, "ios", "itms-services://?action=download-manifest&url=https://sign.example.com/manifest.plist")).GetProperty("id").GetInt32();
        var moved = await visitor.SendAsync(Request("/app/download/ios", IphoneSafari), Ct);
        Assert.Equal(HttpStatusCode.Redirect, moved.StatusCode);
        Assert.StartsWith("itms-services://", moved.Headers.Location!.OriginalString);
        // Withdrawn again (the other tests here share the database): back to the profile.
        await admin.AdminSendAsync(HttpMethod.Delete, $"/api/admin/app-releases/{link}");
        Assert.Equal(HttpStatusCode.OK, (await visitor.SendAsync(Request("/app/download/ios", IphoneSafari), Ct)).StatusCode);

        static string Uuids(string plist) => string.Join(",", plist.Split("<key>PayloadUUID</key>").Skip(1).Select(p => p.Split("</string>")[0]));
    }

    [Fact]
    public async Task The_profile_is_signed_when_a_certificate_is_configured()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=huangyue.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var pfx = Path.Combine(Path.GetTempPath(), $"reelguo-profile-{Guid.NewGuid():N}.pfx");
        await File.WriteAllBytesAsync(pfx, cert.Export(X509ContentType.Pfx, "pw"), Ct);
        try
        {
            await using var api = db.CreateApi(new Dictionary<string, string?>
            {
                ["App:ProfileCertificatePath"] = pfx, ["App:ProfileCertificatePassword"] = "pw",
            });
            var r = await api.CreateClient().SendAsync(Request("/app/download/ios", IphoneSafari), Ct);
            Assert.Equal("application/x-apple-aspen-config", r.Content.Headers.ContentType?.MediaType);
            var cms = new SignedCms();
            cms.Decode(await r.Content.ReadAsByteArrayAsync(Ct));
            cms.CheckSignature(verifySignatureOnly: true);
            Assert.Equal(cert.Thumbprint, cms.SignerInfos[0].Certificate!.Thumbprint);
            Assert.Contains("com.apple.webClip.managed", Encoding.UTF8.GetString(cms.ContentInfo.Content));
        }
        finally
        {
            File.Delete(pfx);
        }
    }
}
