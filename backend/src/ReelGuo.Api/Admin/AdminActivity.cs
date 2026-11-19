using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http.HttpResults;
using ReelGuo.Api.Infrastructure;

namespace ReelGuo.Api.Admin;

/// <param name="Kind"><c>optimize</c> (优化视频, in the admin's browser) or <c>upload</c> (文件夹上传).</param>
/// <param name="Detail">Progress for the operator, e.g. "12/40".</param>
/// <param name="Active">False when the work has finished or stopped.</param>
public sealed record AdminActivityRequest(string Kind, string? Detail, bool Active);

public sealed record BusyItemDto(string Admin, string Kind, string Detail, int SecondsAgo);

public sealed record BusyDto(bool Busy, List<BusyItemDto> Items);

/// <summary>
/// Long-running admin work that a restart would cut off: optimizing videos (in the browser) and folder uploads. The
/// admin console reports it every 30 s while it runs; scripts/staging/update.ps1 reads /health/busy and waits before
/// stopping the services. Kept in memory: a restart is exactly what it guards against, so nothing needs to survive one.
/// </summary>
public sealed class AdminActivity(TimeProvider time)
{
    /// <summary>A report older than this is stale: the tab was closed or the PC went to sleep.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(90);

    public static readonly string[] Kinds = ["optimize", "upload"];

    private readonly ConcurrentDictionary<(string Admin, string Kind), (string Detail, DateTimeOffset At)> _seen = new();

    public void Report(string admin, string kind, string detail, bool active)
    {
        if (active) _seen[(admin, kind)] = (detail, time.GetUtcNow());
        else _seen.TryRemove((admin, kind), out _);
    }

    public BusyDto Current()
    {
        var now = time.GetUtcNow();
        var items = new List<BusyItemDto>();
        foreach (var (key, value) in _seen)
        {
            if (now - value.At > Window) { _seen.TryRemove(key, out _); continue; }
            items.Add(new(key.Admin, key.Kind, value.Detail, (int)(now - value.At).TotalSeconds));
        }
        return new(items.Count > 0, items.OrderBy(i => i.Admin).ThenBy(i => i.Kind).ToList());
    }
}

public static class AdminActivityEndpoints
{
    public static RouteGroupBuilder MapAdminActivity(this RouteGroupBuilder admin)
    {
        admin.MapPost("/activity", Report).WithTags("Admin")
            .WithSummary("Heartbeat from the admin console while it optimizes or uploads videos (updates wait for it).");
        return admin;
    }

    /// <summary>
    /// For scripts/staging/update.ps1 on the server itself: answers only requests made straight to the API on this
    /// machine, never ones that came through Caddy or Cloudflare (they carry forwarding headers), so visitors can't see it.
    /// </summary>
    public static IEndpointRouteBuilder MapBusyCheck(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health/busy", (HttpContext http, AdminActivity activity) =>
            IsDirectLocal(http) ? Results.Ok(activity.Current()) : Results.NotFound()).ExcludeFromDescription();
        return app;
    }

    internal static bool IsDirectLocal(HttpContext http)
    {
        var remote = http.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote)) return false;
        var h = http.Request.Headers;
        return !h.ContainsKey("X-Forwarded-For") && !h.ContainsKey("X-Original-For") && !h.ContainsKey("X-Real-IP") && !h.ContainsKey("CF-Connecting-IP");
    }

    private static Results<NoContent, BadRequest<ApiErrorResponse>> Report(AdminActivityRequest request, HttpContext http, AdminActivity activity)
    {
        if (!AdminActivity.Kinds.Contains(request.Kind)) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_kind", "未知的任务类型"));
        var detail = (request.Detail ?? "").Trim();
        activity.Report(AdminActor.From(http), request.Kind, detail.Length > 60 ? detail[..60] : detail, request.Active);
        return TypedResults.NoContent();
    }
}
