using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Data;
using ReelGuo.Data.Catalog;
using ReelGuo.Data.Entities;
using ReelGuo.Data.Jobs;
using ReelGuo.Data.Storage;

namespace ReelGuo.Api.Admin;

public sealed record IncomingUploadRequest(string FileName, string? ContentType);

public sealed record IncomingFileDto(string Name, string Key);

public sealed record ImportRequest(string Csv, bool DryRun);

public sealed record ImportSeriesLine(string ExternalId, string Title, string Action, long? SeriesId);

public sealed record ImportEpisodeLine(int Row, string ExternalId, int Episode, string File, string Action);

public sealed record ImportReport(
    bool DryRun, bool Ok, List<string> Errors, List<ImportSeriesLine> Series, List<ImportEpisodeLine> Episodes, int QueuedJobs);

/// <summary>
/// Bulk import from a CSV manifest: one row per episode; series fields come from the first row of each
/// external_id. Video files are placed in the raw bucket under incoming/ first. Re-running is safe:
/// series are matched by external_id and existing episodes are skipped.
/// </summary>
public static class AdminImportEndpoints
{
    public static readonly string[] Columns =
    [
        "external_id", "series_title", "category", "episode", "episode_title", "file", "description", "tags", "author",
        "score", "total_episodes", "is_finished", "is_original", "is_free", "rights_holder", "contract_ref",
        "licence_start", "licence_end", "territories", "ai_generated", "has_real_person", "consent_ref",
        "episode_price", "free_episodes",
    ];

    private static readonly string[] Required = ["external_id", "series_title", "category", "episode", "file"];

    public static RouteGroupBuilder MapAdminImport(this RouteGroupBuilder admin)
    {
        var import = admin.MapGroup("/import").WithTags("Admin: bulk import");
        import.MapGet("/template", () => TypedResults.Text(string.Join(',', Columns) + "\n", "text/csv"))
            .WithSummary("Manifest header row (CSV).");
        import.MapGet("/incoming", ListIncoming).WithSummary("Files waiting in incoming/.");
        import.MapPost("/incoming", UploadIncoming).WithSummary("Returns where to upload one file into incoming/.");
        import.MapPost("/", Run).WithSummary("Validates a manifest (dry_run) or imports it.");
        return admin;
    }

    private static async Task<Ok<List<IncomingFileDto>>> ListIncoming(StorageBuckets storage, CancellationToken ct)
    {
        var keys = await storage.Raw.ListAsync(StorageKeys.IncomingPrefix, ct);
        return TypedResults.Ok(keys.Select(k => new IncomingFileDto(k[StorageKeys.IncomingPrefix.Length..], k)).ToList());
    }

    private static Results<Ok<UploadTicket>, BadRequest<ApiErrorResponse>> UploadIncoming(IncomingUploadRequest request, StorageBuckets storage)
    {
        if (string.IsNullOrWhiteSpace(request.FileName))
            return TypedResults.BadRequest(ApiErrorResponse.Of("file_required", "请选择文件"));
        var key = StorageKeys.IncomingPrefix + StorageKeys.SanitizeFileName(request.FileName);
        return TypedResults.Ok(AdminCatalogEndpoints.TicketFor(storage, key, request.ContentType));
    }

    private sealed record SeriesPlan(string ExternalId, Dictionary<string, string> Fields, int Row);

    private static async Task<Ok<ImportReport>> Run(
        ImportRequest request, HttpContext http, ReelGuoDbContext db, StorageBuckets storage, MediaJobQueue queue,
        TimeProvider time, CancellationToken ct)
    {
        var errors = new List<string>();
        var (headers, rows) = Csv.ParseWithHeader(request.Csv ?? "");
        foreach (var column in Required.Where(c => !headers.Contains(c)))
            errors.Add($"缺少列：{column}");
        if (rows.Count == 0) errors.Add("清单没有数据行");
        if (errors.Count > 0) return TypedResults.Ok(new ImportReport(request.DryRun, false, errors, [], [], 0));

        var categories = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Slug, ct);
        var incoming = (await storage.Raw.ListAsync(StorageKeys.IncomingPrefix, ct)).ToHashSet(StringComparer.Ordinal);

        // Validate every row and group them by series.
        var seriesPlans = new Dictionary<string, SeriesPlan>(StringComparer.Ordinal);
        var episodeRows = new List<(int Row, string ExternalId, int Episode, string File, Dictionary<string, string> Fields)>();
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var line = i + 2; // header is line 1
            string Get(string key) => r.GetValueOrDefault(key, "");

            var externalId = Get("external_id");
            if (externalId.Length == 0) { errors.Add($"第 {line} 行：external_id 为空"); continue; }
            if (!int.TryParse(Get("episode"), out var ep) || ep < 1) { errors.Add($"第 {line} 行：episode 需为正整数"); continue; }
            var file = Get("file").Replace('\\', '/').TrimStart('/');
            if (file.StartsWith(StorageKeys.IncomingPrefix, StringComparison.Ordinal)) file = file[StorageKeys.IncomingPrefix.Length..];
            if (!incoming.Contains(StorageKeys.IncomingPrefix + file)) errors.Add($"第 {line} 行：incoming/ 中找不到文件 {file}");
            if (episodeRows.Any(e => e.ExternalId == externalId && e.Episode == ep)) errors.Add($"第 {line} 行：{externalId} 第 {ep} 集重复");

            if (!seriesPlans.ContainsKey(externalId))
            {
                seriesPlans[externalId] = new SeriesPlan(externalId, r, line);
                errors.AddRange(ValidateSeriesFields(r, line, categories));
            }
            episodeRows.Add((line, externalId, ep, file, r));
        }

        var externalIds = seriesPlans.Keys.ToList();
        var existingSeries = await db.Series
            .Where(s => s.ExternalId != null && externalIds.Contains(s.ExternalId))
            .ToDictionaryAsync(s => s.ExternalId!, ct);
        var existingSeriesIds = existingSeries.Values.Select(s => s.Id).ToList();
        var existingEpisodes = (await db.Episodes
            .Where(e => existingSeriesIds.Contains(e.SeriesId))
            .Select(e => new { e.SeriesId, e.EpisodeNumber })
            .ToListAsync(ct)).Select(e => (e.SeriesId, e.EpisodeNumber)).ToHashSet();

        var seriesLines = seriesPlans.Values.Select(p => new ImportSeriesLine(
            p.ExternalId, p.Fields.GetValueOrDefault("series_title", ""),
            existingSeries.TryGetValue(p.ExternalId, out var s) ? "update" : "create",
            existingSeries.TryGetValue(p.ExternalId, out var s2) ? s2.Id : null)).ToList();
        var episodeLines = episodeRows.Select(e => new ImportEpisodeLine(e.Row, e.ExternalId, e.Episode, e.File,
            existingSeries.TryGetValue(e.ExternalId, out var s) && existingEpisodes.Contains((s.Id, e.Episode)) ? "skip_exists" : "create")).ToList();

        if (errors.Count > 0 || request.DryRun)
            return TypedResults.Ok(new ImportReport(request.DryRun, errors.Count == 0, errors, seriesLines, episodeLines, 0));

        // Apply.
        var now = time.GetUtcNow();
        var seriesByExternalId = new Dictionary<string, Series>(StringComparer.Ordinal);
        foreach (var plan in seriesPlans.Values)
        {
            if (!existingSeries.TryGetValue(plan.ExternalId, out var series))
            {
                series = new Series { Title = "", Status = SeriesStatus.Draft, ExternalId = plan.ExternalId, CreatedAt = now };
                db.Series.Add(series);
            }
            await ApplySeriesFieldsAsync(series, plan.Fields, categories, db, now, ct);
            series.UpdatedAt = now;
            seriesByExternalId[plan.ExternalId] = series;
        }
        await db.SaveChangesAsync(ct);

        var newEpisodes = new List<Episode>();
        foreach (var e in episodeRows)
        {
            var series = seriesByExternalId[e.ExternalId];
            if (existingEpisodes.Contains((series.Id, e.Episode))) continue;
            var episode = new Episode
            {
                SeriesId = series.Id,
                EpisodeNumber = e.Episode,
                Title = e.Fields.GetValueOrDefault("episode_title", ""),
                IsFree = ParseBool(e.Fields.GetValueOrDefault("is_free", "")) ?? false,
                Status = EpisodeStatus.Processing,
                SourceKey = StorageKeys.IncomingPrefix + e.File,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Episodes.Add(episode);
            newEpisodes.Add(episode);
        }
        await db.SaveChangesAsync(ct);

        foreach (var episode in newEpisodes) await queue.EnqueueTranscodeAsync(episode.Id, ct);
        db.AddAudit(now, AdminActor.From(http), "import.run", "import", now.ToUnixTimeSeconds().ToString(),
            new { series = seriesPlans.Count, episodes = newEpisodes.Count });
        await db.SaveChangesAsync(ct);

        seriesLines = seriesLines.Select(l => l with { SeriesId = seriesByExternalId[l.ExternalId].Id }).ToList();
        return TypedResults.Ok(new ImportReport(false, true, [], seriesLines, episodeLines, newEpisodes.Count));
    }

    private static IEnumerable<string> ValidateSeriesFields(Dictionary<string, string> r, int line, Dictionary<string, Category> categories)
    {
        string Get(string key) => r.GetValueOrDefault(key, "");
        var title = Get("series_title");
        if (title.Length is 0 or > 200) yield return $"第 {line} 行：series_title 需为 1-200 个字符";
        if (!categories.TryGetValue(Get("category"), out var category))
            yield return $"第 {line} 行：频道 {Get("category")} 不存在";
        else if (category.Status != CategoryStatus.Active)
            yield return $"第 {line} 行：频道「{category.Name}」未开放，不能导入内容";
        if (Get("score") is { Length: > 0 } score && (!double.TryParse(score, CultureInfo.InvariantCulture, out var v) || v is < 0 or > 10))
            yield return $"第 {line} 行：score 需在 0-10 之间";
        if (Get("total_episodes") is { Length: > 0 } total && !int.TryParse(total, out _))
            yield return $"第 {line} 行：total_episodes 需为整数";
        foreach (var key in new[] { "licence_start", "licence_end" })
            if (Get(key) is { Length: > 0 } date && !DateOnly.TryParseExact(date, "yyyy-MM-dd", out _))
                yield return $"第 {line} 行：{key} 需为 yyyy-MM-dd 格式";
        if (PublishRules.NormalizeTerritories(SplitList(Get("territories"))) is null)
            yield return $"第 {line} 行：territories 需为 ISO 国家代码（用 | 分隔）或 *";
        foreach (var key in new[] { "is_finished", "is_original", "is_free", "ai_generated", "has_real_person" })
            if (Get(key) is { Length: > 0 } b && ParseBool(b) is null)
                yield return $"第 {line} 行：{key} 需为 是/否（yes/no）";
        if (Get("episode_price") is { Length: > 0 } price && (!int.TryParse(price, out var coins) || coins is < 0 or > Pricing.MaxEpisodePrice))
            yield return $"第 {line} 行：episode_price 需为 0-{Pricing.MaxEpisodePrice} 的整数（金币）";
        if (Get("free_episodes") is { Length: > 0 } free && (!int.TryParse(free, out var n) || n < 0))
            yield return $"第 {line} 行：free_episodes 需为非负整数";
    }

    private static async Task ApplySeriesFieldsAsync(
        Series series, Dictionary<string, string> r, Dictionary<string, Category> categories, ReelGuoDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        string Get(string key) => r.GetValueOrDefault(key, "");
        series.Title = Get("series_title");
        series.CategoryId = categories[Get("category")].Id;
        if (Get("description") is { Length: > 0 } description) series.Description = description;
        if (Get("tags") is { Length: > 0 } tags) series.Tags = SplitList(tags).Take(10).ToList();
        if (Get("score") is { Length: > 0 } score) series.Score = double.Parse(score, CultureInfo.InvariantCulture);
        if (Get("total_episodes") is { Length: > 0 } total) series.TotalEpisodes = int.Parse(total, CultureInfo.InvariantCulture);
        if (ParseBool(Get("is_finished")) is { } finished) series.IsFinished = finished;
        if (ParseBool(Get("is_original")) is { } original) series.IsOriginal = original;
        if (Get("rights_holder") is { Length: > 0 } holder) series.RightsHolder = holder;
        if (Get("contract_ref") is { Length: > 0 } contract) series.ContractRef = contract;
        if (Get("licence_start") is { Length: > 0 } start) series.LicenceStart = DateOnly.ParseExact(start, "yyyy-MM-dd");
        if (Get("licence_end") is { Length: > 0 } end) series.LicenceEnd = DateOnly.ParseExact(end, "yyyy-MM-dd");
        if (Get("territories") is { Length: > 0 } territories) series.Territories = PublishRules.NormalizeTerritories(SplitList(territories))!;
        if (ParseBool(Get("ai_generated")) is { } ai) series.AiGenerated = ai;
        if (ParseBool(Get("has_real_person")) is { } person) series.HasRealPerson = person;
        if (Get("consent_ref") is { Length: > 0 } consent) series.ConsentRef = consent;
        if (Get("episode_price") is { Length: > 0 } price) series.EpisodePrice = int.Parse(price, CultureInfo.InvariantCulture);
        if (Get("free_episodes") is { Length: > 0 } free) series.FreeEpisodes = int.Parse(free, CultureInfo.InvariantCulture);
        if (Get("author") is { Length: > 0 } author) series.Author = await SeriesStats.FindOrCreateAuthorAsync(db, author, now, ct);
    }

    private static IEnumerable<string> SplitList(string value) =>
        value.Split(['|', '、', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static bool? ParseBool(string value) => value.Trim().ToLowerInvariant() switch
    {
        "yes" or "y" or "true" or "1" or "是" => true,
        "no" or "n" or "false" or "0" or "否" => false,
        _ => null,
    };
}
