using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelGuo.Data;
using ReelGuo.Data.Entities;
using ReelGuo.Data.Telemetry;

namespace ReelGuo.Api.Admin;

/// <summary>
/// What the store keeps and the last 24 hours at a glance. <c>store</c>: traces and logs kept in the database
/// (Telemetry:Local); <c>export</c>: also sent to an OTLP backend (OTEL_EXPORTER_OTLP_ENDPOINT).
/// </summary>
public sealed record TelemetryOverviewDto(
    bool Store, bool Export, int RetentionDays, int SlowMs, double SampleRate,
    int FailedTraces24h, int SlowTraces24h, int Errors24h, int Warnings24h,
    DateTimeOffset? LastWrite, long Dropped, string? WriterError);

/// <summary>One request or job. <c>is_error</c> also covers an error logged inside it.</summary>
public sealed record TraceSummaryDto(
    string TraceId, string Service, string Name, DateTimeOffset StartedAt, double DurationMs,
    bool IsError, string? StatusMessage, int? StatusCode, int Spans);

public sealed record TracePageDto(List<TraceSummaryDto> Items, bool More);

public sealed record SpanDto(
    string SpanId, string? ParentSpanId, string Service, string Name, string Kind, DateTimeOffset StartedAt, double DurationMs,
    bool IsError, string? StatusMessage, Dictionary<string, string> Attributes);

/// <summary>level: warning, error or critical.</summary>
public sealed record TelemetryLogDto(long Id, DateTimeOffset At, string Service, string Level, string Category, string Message, string? Exception, string? TraceId);

public sealed record TraceDetailDto(string TraceId, List<SpanDto> Spans, List<TelemetryLogDto> Logs);

public sealed record LogPageDto(List<TelemetryLogDto> Items, bool More);

/// <summary>运行追踪: failed and slow requests and jobs, step by step, and the warning/error log (LocalTelemetry.cs).</summary>
public static partial class AdminTelemetryEndpoints
{
    private const int TracePage = 50;
    private const int LogPage = 100;

    public static RouteGroupBuilder MapAdminTelemetry(this RouteGroupBuilder admin)
    {
        var g = admin.MapGroup("/telemetry").WithTags("Admin: telemetry");
        g.MapGet("/", Overview).WithSummary("What is stored, and the last 24 hours at a glance.");
        g.MapGet("/traces", Traces)
            .WithSummary("Requests and jobs, newest first. show: problems (failed, error logged or slow; default), errors, slow, all.");
        g.MapGet("/traces/{traceId}", Trace).WithSummary("Every stored step of one trace, with the log lines written in it.");
        g.MapGet("/logs", Logs).WithSummary("Warning and error log lines, newest first. level: warning, error (incl. critical), or all.");
        return admin;
    }

    private static LocalTelemetryOptions Options(HttpContext http) =>
        http.RequestServices.GetService<IOptions<LocalTelemetryOptions>>()?.Value ?? new LocalTelemetryOptions();

    private static async Task<Ok<TelemetryOverviewDto>> Overview(ReelGuoDbContext db, IConfiguration configuration, HttpContext http, TimeProvider time, CancellationToken ct)
    {
        var o = Options(http);
        var status = http.RequestServices.GetService<LocalTelemetryStatus>();
        var since = time.GetUtcNow().AddDays(-1);
        var roots = db.TelemetrySpans.AsNoTracking().Where(s => s.IsRoot && s.StartedAt >= since);
        var logs = db.TelemetryLogs.AsNoTracking().Where(l => l.At >= since);
        return TypedResults.Ok(new TelemetryOverviewDto(
            LocalTelemetrySetup.Enabled(configuration), ReelGuoTelemetry.ExportEnabled(configuration), o.RetentionDays, o.SlowMs, o.SampleRate,
            await roots.CountAsync(s => s.IsError, ct),
            await roots.CountAsync(s => s.DurationMs >= o.SlowMs, ct),
            await logs.CountAsync(l => l.Level >= 4, ct),
            await logs.CountAsync(l => l.Level == 3, ct),
            status?.LastWrite, status?.Dropped ?? 0, status?.LastError));
    }

    private static async Task<Ok<TracePageDto>> Traces(
        ReelGuoDbContext db, HttpContext http, TimeProvider time, CancellationToken ct,
        string? show = null, string? service = null, string? q = null, int hours = 24, DateTimeOffset? before = null)
    {
        var slow = Options(http).SlowMs;
        var since = time.GetUtcNow().AddHours(-Math.Clamp(hours, 1, 24 * 31));
        var query = db.TelemetrySpans.AsNoTracking().Where(s => s.IsRoot && s.StartedAt >= since);
        if (!string.IsNullOrWhiteSpace(service)) query = query.Where(s => s.Service == service);
        if (before is { } b) query = query.Where(s => s.StartedAt < b);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var text = q.Trim();
            query = TraceIdPattern().IsMatch(text)
                ? query.Where(s => s.TraceId == text.ToLowerInvariant())
                : query.Where(s => EF.Functions.ILike(s.Name, Like(text), "\\") || (s.StatusMessage != null && EF.Functions.ILike(s.StatusMessage, Like(text), "\\")));
        }
        query = (show ?? "problems") switch
        {
            "errors" => query.Where(s => s.IsError || db.TelemetryLogs.Any(l => l.TraceId == s.TraceId && l.Level >= 4)),
            "slow" => query.Where(s => s.DurationMs >= slow),
            "all" => query,
            _ => query.Where(s => s.IsError || s.DurationMs >= slow || db.TelemetryLogs.Any(l => l.TraceId == s.TraceId && l.Level >= 4)),
        };
        var rows = await query.OrderByDescending(s => s.StartedAt).Take(TracePage + 1)
            .Select(s => new
            {
                s.TraceId, s.Service, s.Name, s.StartedAt, s.DurationMs, s.StatusMessage, s.Attributes,
                IsError = s.IsError || db.TelemetryLogs.Any(l => l.TraceId == s.TraceId && l.Level >= 4),
                Spans = db.TelemetrySpans.Count(x => x.TraceId == s.TraceId),
            })
            .ToListAsync(ct);
        var items = rows.Take(TracePage).Select(r => new TraceSummaryDto(
            r.TraceId, r.Service, r.Name, r.StartedAt, r.DurationMs, r.IsError, r.StatusMessage, StatusCode(r.Attributes), r.Spans)).ToList();
        return TypedResults.Ok(new TracePageDto(items, rows.Count > TracePage));
    }

    private static async Task<Results<Ok<TraceDetailDto>, NotFound>> Trace(string traceId, ReelGuoDbContext db, CancellationToken ct)
    {
        var id = traceId.Trim().ToLowerInvariant();
        var spans = await db.TelemetrySpans.AsNoTracking().Where(s => s.TraceId == id).OrderBy(s => s.StartedAt).ToListAsync(ct);
        var logs = await db.TelemetryLogs.AsNoTracking().Where(l => l.TraceId == id).OrderBy(l => l.At).ToListAsync(ct);
        if (spans.Count == 0 && logs.Count == 0) return TypedResults.NotFound();
        return TypedResults.Ok(new TraceDetailDto(id,
            spans.Select(s => new SpanDto(s.SpanId, s.ParentSpanId, s.Service, s.Name, s.Kind, s.StartedAt, s.DurationMs, s.IsError, s.StatusMessage, Attributes(s.Attributes))).ToList(),
            logs.Select(ToDto).ToList()));
    }

    private static async Task<Ok<LogPageDto>> Logs(
        ReelGuoDbContext db, TimeProvider time, CancellationToken ct,
        string? level = null, string? service = null, string? q = null, int hours = 24, DateTimeOffset? before = null)
    {
        var since = time.GetUtcNow().AddHours(-Math.Clamp(hours, 1, 24 * 31));
        var query = db.TelemetryLogs.AsNoTracking().Where(l => l.At >= since);
        query = level switch
        {
            "error" => query.Where(l => l.Level >= 4),
            "warning" => query.Where(l => l.Level == 3),
            _ => query,
        };
        if (!string.IsNullOrWhiteSpace(service)) query = query.Where(l => l.Service == service);
        if (before is { } b) query = query.Where(l => l.At < b);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = Like(q.Trim());
            query = query.Where(l => EF.Functions.ILike(l.Message, pattern, "\\") || EF.Functions.ILike(l.Category, pattern, "\\")
                || (l.Exception != null && EF.Functions.ILike(l.Exception, pattern, "\\")));
        }
        var rows = await query.OrderByDescending(l => l.At).ThenByDescending(l => l.Id).Take(LogPage + 1).ToListAsync(ct);
        return TypedResults.Ok(new LogPageDto(rows.Take(LogPage).Select(ToDto).ToList(), rows.Count > LogPage));
    }

    private static TelemetryLogDto ToDto(TelemetryLog l) =>
        new(l.Id, l.At, l.Service, l.Level switch { >= 5 => "critical", 4 => "error", _ => "warning" }, l.Category, l.Message, l.Exception, l.TraceId);

    private static string Like(string text) => "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    private static Dictionary<string, string> Attributes(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The HTTP status of a request (server spans), when recorded.</summary>
    private static int? StatusCode(string json) =>
        Attributes(json).TryGetValue("http.response.status_code", out var v) && int.TryParse(v, out var code) ? code : null;

    [GeneratedRegex("^[0-9a-fA-F]{32}$")]
    private static partial Regex TraceIdPattern();
}
