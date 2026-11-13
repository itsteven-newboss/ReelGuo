using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Api.Public;
using ReelGuo.Data;
using ReelGuo.Data.Entities;

namespace ReelGuo.Api.Admin;

public sealed record AdminAppReleaseDto(
    int Id, string Platform, string Version, string Url, string? Signature, string? Sha256, string Notes, bool Mandatory, bool Published,
    DateTimeOffset? PublishedAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string UpdatedBy);

public sealed record AppReleaseInput(string Platform, string Version, string Url, string? Signature, string? Sha256, string? Notes, bool Mandatory, bool Published);

/// <summary>App 版本: native app builds offered to the desktop updater, the Android update prompt and the download page.</summary>
public static class AdminAppReleasesEndpoints
{
    public static RouteGroupBuilder MapAdminAppReleases(this RouteGroupBuilder admin)
    {
        var releases = admin.MapGroup("/app-releases").WithTags("Admin: app releases");
        releases.MapGet("/", List).WithSummary("Every release, newest first.");
        releases.MapPost("/", Create).WithSummary("Adds a release (unpublished releases are not offered to anyone).");
        releases.MapPut("/{id:int}", Update).WithSummary("Edits, publishes or withdraws a release.");
        releases.MapDelete("/{id:int}", Delete).WithSummary("Deletes a release.");
        return admin;
    }

    private static AdminAppReleaseDto ToDto(AppRelease r) => new(r.Id, r.Platform, r.Version, r.Url, r.Signature, r.Sha256, r.Notes, r.Mandatory,
        r.Published, r.PublishedAt, r.CreatedAt, r.UpdatedAt, r.UpdatedBy);

    private static async Task<Ok<List<AdminAppReleaseDto>>> List(ReelGuoDbContext db, CancellationToken ct) =>
        TypedResults.Ok((await db.AppReleases.AsNoTracking().ToListAsync(ct))
            .OrderBy(r => Array.IndexOf(AppVersions.Platforms, r.Platform))
            .ThenByDescending(r => AppVersions.Parse(r.Version))
            .Select(ToDto).ToList());

    private static string? Validate(AppReleaseInput input)
    {
        var platform = (input.Platform ?? "").Trim().ToLowerInvariant();
        if (!AppVersions.Platforms.Contains(platform)) return $"平台需为 {string.Join(" / ", AppVersions.Platforms)} 之一";
        if (AppVersions.Parse(input.Version) is null) return "版本号需为 主.次.修订 格式，如 0.6.0";
        if (!Uri.TryCreate((input.Url ?? "").Trim(), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps
            && !(url.Scheme == Uri.UriSchemeHttp && url.IsLoopback))
            return "下载地址需为 https:// 开头的完整地址";
        if (AppVersions.IsDesktop(platform) && input.Published && string.IsNullOrWhiteSpace(input.Signature))
            return "桌面版发布前需填写更新签名（.sig 文件内容），否则自动更新会拒绝安装";
        if ((input.Signature ?? "").Length > 2000) return "签名过长";
        if (input.Sha256 is { Length: > 0 } sha && (sha.Trim().Length != 64 || !sha.Trim().All(Uri.IsHexDigit))) return "SHA-256 需为 64 位十六进制";
        if ((input.Notes ?? "").Length > 2000) return "更新说明不超过 2000 字";
        return null;
    }

    private static void Apply(AppRelease r, AppReleaseInput input, DateTimeOffset now, string actor)
    {
        r.Platform = input.Platform.Trim().ToLowerInvariant();
        r.Version = AppVersions.Parse(input.Version)!.ToString(3);
        r.Url = input.Url.Trim();
        r.Signature = string.IsNullOrWhiteSpace(input.Signature) ? null : input.Signature.Trim();
        r.Sha256 = string.IsNullOrWhiteSpace(input.Sha256) ? null : input.Sha256.Trim().ToLowerInvariant();
        r.Notes = (input.Notes ?? "").Trim();
        r.Mandatory = input.Mandatory;
        if (input.Published && !r.Published) r.PublishedAt = now;
        r.Published = input.Published;
        r.UpdatedAt = now;
        r.UpdatedBy = actor;
    }

    private static async Task<Results<Ok<AdminAppReleaseDto>, BadRequest<ApiErrorResponse>, Conflict<ApiErrorResponse>>> Create(
        AppReleaseInput input, HttpContext http, ReelGuoDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (Validate(input) is { } error) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_release", error));
        var now = time.GetUtcNow();
        var actor = AdminActor.From(http);
        var release = new AppRelease { Platform = "", Version = "", Url = "", UpdatedBy = actor, CreatedAt = now };
        Apply(release, input, now, actor);
        if (await db.AppReleases.AnyAsync(r => r.Platform == release.Platform && r.Version == release.Version, ct))
            return TypedResults.Conflict(ApiErrorResponse.Of("release_exists", $"{release.Platform} {release.Version} 已存在"));
        db.AppReleases.Add(release);
        await db.SaveChangesAsync(ct);
        db.AddAudit(now, actor, "app_release.create", "app_release", release.Id.ToString(), new { release.Platform, release.Version, release.Published, release.Mandatory });
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(release));
    }

    private static async Task<Results<Ok<AdminAppReleaseDto>, NotFound<ApiErrorResponse>, BadRequest<ApiErrorResponse>, Conflict<ApiErrorResponse>>> Update(
        int id, AppReleaseInput input, HttpContext http, ReelGuoDbContext db, TimeProvider time, CancellationToken ct)
    {
        var release = await db.AppReleases.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (release is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "版本不存在"));
        if (Validate(input) is { } error) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_release", error));
        var now = time.GetUtcNow();
        var actor = AdminActor.From(http);
        var wasPublished = release.Published;
        Apply(release, input, now, actor);
        if (await db.AppReleases.AnyAsync(r => r.Id != id && r.Platform == release.Platform && r.Version == release.Version, ct))
            return TypedResults.Conflict(ApiErrorResponse.Of("release_exists", $"{release.Platform} {release.Version} 已存在"));
        var action = !wasPublished && release.Published ? "app_release.publish" : wasPublished && !release.Published ? "app_release.withdraw" : "app_release.update";
        db.AddAudit(now, actor, action, "app_release", id.ToString(), new { release.Platform, release.Version, release.Published, release.Mandatory });
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(release));
    }

    private static async Task<Results<NoContent, NotFound<ApiErrorResponse>>> Delete(int id, HttpContext http, ReelGuoDbContext db, TimeProvider time, CancellationToken ct)
    {
        var release = await db.AppReleases.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (release is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "版本不存在"));
        db.AppReleases.Remove(release);
        db.AddAudit(time, AdminActor.From(http), "app_release.delete", "app_release", id.ToString(), new { release.Platform, release.Version });
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }
}
