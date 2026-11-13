using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReelGuo.Api.Admin;
using ReelGuo.Api.Geo;
using ReelGuo.Api.Media;

namespace ReelGuo.Api.Tests;

public static class TestKeys
{
    /// <summary>Base64 of 32 fixed bytes; also used by the edge worker's signature-compatibility test.</summary>
    public const string SigningKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    /// <summary>Viewer access-token key (32 bytes, base64).</summary>
    public const string JwtKey = "ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8=";
    public const string AdminUser = "admin";
    public const string AdminPassword = "test-admin-password";
}

/// <summary>Runs the real API in memory. Database-free by default; pass a connection string for the Postgres tests.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _environment;
    private readonly Dictionary<string, string?> _settings;
    private readonly IBlockedCountryStore? _store;
    private readonly bool _testAdmin;
    private readonly bool _databaseFree;

    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), "reelguo-tests", Guid.NewGuid().ToString("N"));

    public ApiFactory(
        string environment = "Development",
        IReadOnlyDictionary<string, string?>? settings = null,
        IBlockedCountryStore? store = null,
        bool testAdmin = true)
    {
        _environment = environment;
        _store = store;
        _testAdmin = testAdmin;
        _databaseFree = settings?.ContainsKey("ConnectionStrings:Default") != true;
        _settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused",
            ["Database:MigrateOnStartup"] = "false",
            ["Admin:BootstrapUsername"] = "",
            ["Admin:BootstrapPassword"] = "",
            ["Storage:Provider"] = "FileSystem",
            ["Storage:FileSystemRoot"] = StorageRoot,
            ["Media:SigningKey"] = TestKeys.SigningKey,
            ["Media:PublicBaseUrl"] = "/media",
            ["Edge:Secret"] = "test-edge-secret",
            ["Auth:JwtKey"] = TestKeys.JwtKey,
        };
        if (settings is not null)
            foreach (var (key, value) in settings) _settings[key] = value;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(_settings));
        builder.ConfigureServices(services =>
        {
            services.AddLogging(l => l.SetMinimumLevel(LogLevel.Warning));
            // TestServer leaves the peer address empty; real servers always have one (here: a local proxy).
            services.AddSingleton<IStartupFilter>(new PeerAddressStartupFilter());
            if (_databaseFree)
            {
                // Without a database there is nothing taken down; the real store would fail closed.
                services.RemoveAll<IRevokedSeriesStore>();
                services.AddSingleton<IRevokedSeriesStore, NoRevokedSeries>();
                services.RemoveAll<IAgeRuleStore>();
                services.AddSingleton<IAgeRuleStore, NoAgeRules>();
            }
            if (_store is not null)
            {
                services.RemoveAll<IBlockedCountryStore>();
                services.AddSingleton(_store);
            }
            if (_testAdmin)
            {
                // Lets database-free tests act as an admin by sending X-Test-Admin; the cookie scheme still works too.
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAdminHandler>(TestAdminHandler.SchemeName, _ => { });
                services.PostConfigure<AuthorizationOptions>(o => o.AddPolicy(AdminAuth.Policy, p => p
                    .AddAuthenticationSchemes(AdminAuth.Scheme, TestAdminHandler.SchemeName)
                    .RequireAuthenticatedUser()
                    .RequireRole("Admin")));
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (Directory.Exists(StorageRoot)) Directory.Delete(StorageRoot, recursive: true); } catch { /* temp folder */ }
    }
}

public sealed class TestAdminHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string Header = "X-Test-Admin";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Header, out var name)) return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "0"),
            new Claim(ClaimTypes.Name, name.ToString()),
            new Claim("nickname", name.ToString()),
            new Claim(ClaimTypes.Role, "Admin"),
        ], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    // The cookie scheme already writes the 401 body; this test-only scheme must not answer as well.
    protected override Task HandleChallengeAsync(AuthenticationProperties properties) => Task.CompletedTask;
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) => Task.CompletedTask;
}

public sealed class FakeBlockedCountryStore(params string[] codes) : IBlockedCountryStore
{
    public HashSet<string> Codes { get; } = new(codes, StringComparer.Ordinal);

    public ValueTask<IReadOnlySet<string>> GetAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlySet<string>>(Codes);

    public void Invalidate() { }
}

public static class HttpTestExtensions
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    public static async Task<string?> ErrorCodeAsync(this HttpResponseMessage response) =>
        (await response.JsonAsync()).GetProperty("error").GetProperty("code").GetString();

    public static HttpRequestMessage From(this HttpRequestMessage request, string country)
    {
        request.Headers.Add(CountryResolver.DebugHeader, country);
        return request;
    }

    /// <summary>Acts as a signed-in admin (test scheme) and adds the CSRF header.</summary>
    public static HttpRequestMessage AsAdmin(this HttpRequestMessage request, string name = "tester")
    {
        request.Headers.Add(TestAdminHandler.Header, name);
        request.Headers.Add(AdminAuth.CsrfHeader, "1");
        return request;
    }

    public static HttpRequestMessage WithCsrf(this HttpRequestMessage request)
    {
        request.Headers.Add(AdminAuth.CsrfHeader, "1");
        return request;
    }
}

/// <summary>Finds ffmpeg for media tests: REELGUO_FFMPEG_DIR, the repo's .tools/ffmpeg/bin, or PATH.</summary>
public static class FfmpegLocator
{
    private static readonly Lazy<string?> Dir = new(Find);

    /// <summary>Folder with ffmpeg/ffprobe, "" for PATH, or null when ffmpeg is not available.</summary>
    public static string? BinDirectory => Dir.Value;

    public static string Tool(string name) =>
        Path.Combine(BinDirectory ?? "", OperatingSystem.IsWindows() ? name + ".exe" : name);

    private static string? Find()
    {
        var env = Environment.GetEnvironmentVariable("REELGUO_FFMPEG_DIR");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, ".tools", "ffmpeg", "bin");
            if (Directory.Exists(candidate)) return candidate;
        }

        try
        {
            using var p = Process.Start(new ProcessStartInfo("ffmpeg", "-version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(5000);
            return p?.ExitCode == 0 ? "" : null;
        }
        catch
        {
            return null;
        }
    }

    public static void RequireOrSkip()
    {
        if (BinDirectory is null) Assert.Skip("ffmpeg is not available; set REELGUO_FFMPEG_DIR or install ffmpeg.");
    }

    /// <summary>Makes a short test clip with picture and tone.</summary>
    public static async Task<string> MakeClipAsync(string path, int width = 720, int height = 1280, double seconds = 5, bool audio = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var args = new List<string> { "-hide_banner", "-y", "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate=30" };
        if (audio) args.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000"]);
        args.AddRange(["-t", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"]);
        if (audio) args.AddRange(["-c:a", "aac"]);
        args.Add(path);

        var psi = new ProcessStartInfo(Tool("ffmpeg")) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) throw new InvalidOperationException("Could not make test clip: " + err);
        return path;
    }
}

/// <summary>Gives every test request the peer address 127.0.0.1, as a reverse proxy on the same host would have.</summary>
public sealed class PeerAddressStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            context.Connection.RemoteIpAddress ??= System.Net.IPAddress.Loopback;
            return nextMiddleware(context);
        });
        next(app);
    };
}

public sealed class NoRevokedSeries : IRevokedSeriesStore
{
    public ValueTask<IReadOnlySet<long>> GetAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlySet<long>>(new HashSet<long>());
    public void Invalidate() { }
}

public sealed class NoAgeRules : IAgeRuleStore
{
    public ValueTask<ReelGuo.Data.Entities.AgeCheckMode> ModeForAsync(string country, CancellationToken cancellationToken) =>
        ValueTask.FromResult(ReelGuo.Data.Entities.AgeCheckMode.Declaration);
    public void Invalidate() { }
}
