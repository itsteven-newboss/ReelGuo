using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Data;
using ReelGuo.Data.Entities;

namespace ReelGuo.Api.Public;

public sealed class AppOptions
{
    public const string Section = "App";

    /// <summary>
    /// Addresses the native app may load the web app from, in order of preference (e.g. https://app.example.com plus
    /// mirror domains). The app caches this list, so a domain that gets blocked can be replaced without a new build.
    /// </summary>
    public List<string> Mirrors { get; set; } = [];
}

public sealed record AppReleaseDto(string Platform, string Version, string Url, string? Sha256, string Notes, bool Mandatory, DateTimeOffset? PublishedAt);

/// <summary>
/// What the native app checks on start: the current mirror list and the newest release for its platform.
/// <c>update_required</c>: a mandatory release newer than the running version exists.
/// </summary>
public sealed record AppBootstrapDto(List<string> Mirrors, AppReleaseDto? Latest, bool UpdateAvailable, bool UpdateRequired);

/// <summary>Response format of the Tauri updater's dynamic endpoint.</summary>
public sealed record TauriUpdateDto(string Version, string Notes, DateTimeOffset PubDate, string Url, string Signature);

/// <summary>Semantic versions as the apps report them ("0.6.0", "v1.2", "1.0.0-beta.1"; pre-release tags are ignored).</summary>
public static class AppVersions
{
    public static readonly string[] Platforms = ["windows-x86_64", "darwin-aarch64", "darwin-x86_64", "linux-x86_64", "android"];

    public static bool IsDesktop(string platform) => platform != "android";

    public static Version? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var core = value.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        var parts = core.Split('.');
        if (parts.Length is < 1 or > 3) return null;
        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]) || numbers[i] > 99_999) return null;
        return new Version(numbers[0], numbers[1], numbers[2]);
    }
}

public static class AppEndpoints
{
    /// <summary>Not age-gated or content-bearing: the native shell calls these before the web app loads.</summary>
    public static RouteGroupBuilder MapAppReleases(this RouteGroupBuilder v1)
    {
        var app = v1.MapGroup("/app").WithTags("App");
        app.MapGet("/bootstrap", Bootstrap).WithSummary("Mirror list and update status for a native app (platform = android, windows-x86_64, …).");
        app.MapGet("/releases", Latest).WithSummary("The newest published release per platform (download page).");
        // The desktop updater's own format; not part of the web client's contract.
        app.MapGet("/updates/{target}/{arch}/{current}", TauriUpdate).ExcludeFromDescription();
        return v1;
    }

    internal static AppReleaseDto ToDto(AppRelease r) => new(r.Platform, r.Version, r.Url, r.Sha256, r.Notes, r.Mandatory, r.PublishedAt);

    private static async Task<List<AppRelease>> PublishedAsync(ReelGuoDbContext db, string platform, CancellationToken ct) =>
        (await db.AppReleases.AsNoTracking().Where(r => r.Platform == platform && r.Published).ToListAsync(ct))
            .Where(r => AppVersions.Parse(r.Version) is not null)
            .OrderByDescending(r => AppVersions.Parse(r.Version))
            .ToList();

    private static async Task<Results<Ok<AppBootstrapDto>, BadRequest<ApiErrorResponse>>> Bootstrap(
        ReelGuoDbContext db, IOptions<AppOptions> options, [FromQuery] string? platform, [FromQuery] string? version, CancellationToken ct)
    {
        var p = (platform ?? "").Trim().ToLowerInvariant();
        if (!AppVersions.Platforms.Contains(p)) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_platform", "未知的平台"));
        var current = AppVersions.Parse(version);

        var releases = await PublishedAsync(db, p, ct);
        var latest = releases.FirstOrDefault();
        var newer = current is null ? [] : releases.Where(r => AppVersions.Parse(r.Version) > current).ToList();
        var mirrors = options.Value.Mirrors.Select(m => m.Trim().TrimEnd('/')).Where(m => m.Length > 0).Distinct().ToList();
        return TypedResults.Ok(new AppBootstrapDto(mirrors, latest is null ? null : ToDto(latest), newer.Count > 0, newer.Any(r => r.Mandatory)));
    }

    private static async Task<Ok<List<AppReleaseDto>>> Latest(ReelGuoDbContext db, CancellationToken ct)
    {
        var rows = await db.AppReleases.AsNoTracking().Where(r => r.Published).ToListAsync(ct);
        return TypedResults.Ok(rows
            .Where(r => AppVersions.Parse(r.Version) is not null)
            .GroupBy(r => r.Platform)
            .Select(g => g.OrderByDescending(r => AppVersions.Parse(r.Version)).First())
            .OrderBy(r => Array.IndexOf(AppVersions.Platforms, r.Platform))
            .Select(ToDto).ToList());
    }

    /// <summary>
    /// Tauri updater dynamic endpoint: 200 with the newest signed release for {target}-{arch} when it is newer than
    /// {current}, otherwise 204 (no update).
    /// </summary>
    private static async Task<IResult> TauriUpdate(string target, string arch, string current, ReelGuoDbContext db, CancellationToken ct)
    {
        var platform = $"{target}-{arch}".ToLowerInvariant();
        if (!AppVersions.Platforms.Contains(platform) || !AppVersions.IsDesktop(platform) || AppVersions.Parse(current) is not { } running)
            return Results.NoContent();
        var latest = (await PublishedAsync(db, platform, ct)).FirstOrDefault(r => !string.IsNullOrEmpty(r.Signature));
        if (latest is null || AppVersions.Parse(latest.Version) <= running) return Results.NoContent();
        return TypedResults.Ok(new TauriUpdateDto(latest.Version, latest.Notes, latest.PublishedAt ?? latest.UpdatedAt, latest.Url, latest.Signature!));
    }
}
