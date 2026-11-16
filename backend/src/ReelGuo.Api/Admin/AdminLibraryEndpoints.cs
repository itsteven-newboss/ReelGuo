using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Data;
using ReelGuo.Data.Catalog;
using ReelGuo.Data.Entities;
using ReelGuo.Data.Jobs;
using ReelGuo.Data.Media;
using ReelGuo.Data.Storage;

namespace ReelGuo.Api.Admin;

// ---------- what the admin's browser found in the folder ----------

/// <summary>One episode file, with what its MP4 header says (read in the browser, a few kilobytes per file).</summary>
public sealed record LibraryVideoFile(
    string Name, long Bytes, double DurationSec, int Width, int Height, bool Complete, bool Faststart, string? VideoCodec, string? AudioCodec);

/// <summary>A title's folder under Videos/: its finished files, and the downloader's placeholders still arriving.</summary>
public sealed record LibraryFolder(string Name, List<LibraryVideoFile>? Files, List<string>? Downloading);

public sealed record LibraryImage(string Name, long Bytes, int Width, int Height);

/// <summary>A details file from VideoDetails/ (reelpico.series/1 JSON), as text.</summary>
public sealed record LibraryDetails(string Name, string? Text);

/// <summary>The delivery folder as the browser lists it: VideoDetails/, Images/ and Videos/, tied together only by names.</summary>
public sealed record LibraryListing(List<LibraryDetails>? Details, List<LibraryImage>? Images, List<LibraryFolder>? Folders);

public sealed record LibraryPlanRequest(string? StorageFolder, LibraryListing Listing);

// ---------- what the server decides ----------

/// <param name="State">
/// new (to upload), stored (already in COS at its key: linked without uploading), failed (to upload again),
/// uploaded, processing, changed (differs from what the site has; replaced only on request), incomplete
/// (still downloading or cut short), unplayable (COS plays files as they are: H.264 + AAC, faststart),
/// held (above the title's episode total, or the series was removed), duplicate, unnumbered.
/// </param>
public sealed record LibraryEpisodePlan(int Number, string FileName, long Bytes, string State, long? EpisodeId, string? Note);

/// <param name="Status">new, updates, synced or attention.</param>
public sealed record LibraryTitlePlan(
    string Key, string Title, string ExternalId, string? FolderName, string? ImageName, string? DetailsName,
    long? SeriesId, SeriesStatus? SeriesStatus, bool HasCover, int? TotalEpisodes, string Status, List<string> Problems,
    List<LibraryEpisodePlan> Episodes, int Downloading);

public sealed record LibraryPlan(string StorageFolder, bool Cos, List<LibraryTitlePlan> Titles, string? Warning);

public sealed record LibraryConfigDto(bool Cos, string? Bucket, string? Region, string DefaultFolder, List<string> Folders, string? Warning);

/// <summary>Rights details applied to every series the upload creates, where the series has none yet.</summary>
public sealed record LibraryRights(
    string? RightsHolder, string? ContractRef, DateOnly? LicenceStart, List<string>? Territories, bool? AiGenerated, bool? HasRealPerson);

public sealed record LibraryApplyRequest(string CategorySlug, LibraryListing Listing, LibraryRights? Rights);

public sealed record LibraryApplyResponse(long SeriesId, bool Created, string Title);

public sealed record LibraryEpisodeStartRequest(
    long SeriesId, int Number, string? StorageFolder, string TitleFolder, string FileName, long Bytes, string? ContentType, bool Replace);

/// <param name="Stored">The file is already in COS at its key (same size): nothing to upload, commit straight away.</param>
public sealed record LibraryEpisodeTicket(long EpisodeId, bool Stored, UploadTicket? Upload);

public sealed record LibraryCommitRequest(long Bytes);

/// <summary>
/// Folder upload, as on the Viora platform's delivery page (docs/ARCHITECTURE.md §9.14): the admin's browser lists the
/// delivery folder, the server decides what is missing and hands out one-time upload links, the browser sends each
/// file straight to storage (COS: "videos/{storage folder}/{title folder}/{file}"), and the server confirms it with
/// storage before the worker takes it. Video never passes through this server.
/// </summary>
public static class AdminLibraryEndpoints
{
    private static readonly string[] PlayableVideo = ["avc1", "avc3"];

    public static RouteGroupBuilder MapAdminLibrary(this RouteGroupBuilder admin)
    {
        var library = admin.MapGroup("/library").WithTags("Admin: library upload");
        library.MapGet("/", Config).WithSummary("Where uploads go, and the storage folders already in the bucket.");
        library.MapPost("/plan", Plan).WithSummary("Compares the browser's folder listing with the catalogue and says what to upload.");
        library.MapPost("/titles", Apply).WithSummary("Creates the draft series for one title of the listing, or fills in what it lacks.");
        library.MapPost("/episodes", Start).WithSummary("Creates or reuses the episode and returns where to upload its file.");
        library.MapPost("/episodes/{id:long}/commit", Commit).WithSummary("Confirms the upload with storage and queues the episode for the worker.");
        library.MapPost("/episodes/{id:long}/abort", Abort).WithSummary("Records that an upload did not finish.");
        return admin;
    }

    // ---------- config ----------

    private static async Task<Ok<LibraryConfigDto>> Config(CosVideoStore cos, ILoggerFactory logs, CancellationToken ct)
    {
        if (!cos.Enabled) return TypedResults.Ok(new LibraryConfigDto(false, null, null, cos.DefaultFolder, [], null));
        try
        {
            var folders = await cos.ListFoldersAsync(ct);
            return TypedResults.Ok(new LibraryConfigDto(true, cos.Bucket, cos.Region, cos.DefaultFolder, [.. folders], null));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            logs.CreateLogger("AdminLibrary").LogWarning(ex, "Could not list the COS bucket's folders");
            return TypedResults.Ok(new LibraryConfigDto(true, cos.Bucket, cos.Region, cos.DefaultFolder, [],
                "读取 COS 存储桶失败，请检查 COS 设置（密钥、存储桶名称、地域）"));
        }
    }

    // ---------- plan ----------

    private static async Task<Results<Ok<LibraryPlan>, BadRequest<ApiErrorResponse>>> Plan(
        LibraryPlanRequest request, ReelGuoDbContext db, CosVideoStore cos, ILoggerFactory logs, CancellationToken ct)
    {
        if (Validate(request.Listing) is { } invalid) return TypedResults.BadRequest(invalid);
        var folder = StorageFolder(request.StorageFolder, cos);
        if (folder is null) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_folder", "存储文件夹名称无效（不能含 / ，最多 64 个字符）"));

        var groups = Group(request.Listing);
        var catalogue = await CatalogueAsync(db, ct);
        var matches = groups.ToDictionary(g => g, g => Match(g, catalogue));
        var seriesIds = matches.Values.Where(m => m.Series is not null).Select(m => m.Series!.Id).Distinct().ToList();
        var episodes = (await db.Episodes.AsNoTracking().Where(e => seriesIds.Contains(e.SeriesId))
                .Select(e => new EpisodeRow(e.Id, e.SeriesId, e.EpisodeNumber, e.Status, e.SourceKey, e.VideoKey, e.VideoBytes))
                .ToListAsync(ct))
            .ToLookup(e => e.SeriesId);

        // One bucket listing per title folder says which files are already stored, so they are linked, not sent again.
        string? warning = null;
        var stored = new Dictionary<string, long>(StringComparer.Ordinal);
        if (cos.Enabled)
        {
            try
            {
                using var gate = new SemaphoreSlim(6);
                var lists = await Task.WhenAll(groups.Where(g => g.Folder is not null).Select(async g =>
                {
                    await gate.WaitAsync(ct);
                    try { return await cos.ListFilesAsync(TitlePrefix(folder, g.Folder!.Name), ct); }
                    finally { gate.Release(); }
                }));
                foreach (var list in lists)
                    foreach (var (key, size) in list) stored[key] = size;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
            {
                logs.CreateLogger("AdminLibrary").LogWarning(ex, "Could not list the COS bucket");
                warning = "读取 COS 存储桶失败：无法判断哪些文件已在 COS 中，上传时会再确认。";
            }
        }

        var titles = groups.Select(g => PlanTitle(g, matches[g], episodes, stored, folder, cos.Enabled))
            .OrderBy(t => t.Status switch { "attention" => 0, "new" => 1, "updates" => 2, _ => 3 })
            .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return TypedResults.Ok(new LibraryPlan(folder, cos.Enabled, titles, warning));
    }

    private sealed record EpisodeRow(long Id, long SeriesId, int Number, EpisodeStatus Status, string? SourceKey, string? VideoKey, long? VideoBytes);

    private static LibraryTitlePlan PlanTitle(
        Grouped g, Matched m, ILookup<long, EpisodeRow> episodes, Dictionary<string, long> stored, string folder, bool useCos)
    {
        var problems = new List<string>(g.Problems);
        problems.AddRange(m.Problems);
        var series = m.Series;
        var removed = series?.Status == SeriesStatus.Removed;
        if (removed) problems.Add("该剧已在后台删除，不会再上传（如需恢复请先在内容管理中处理）");
        var total = g.Parsed?.Total ?? (series is { TotalEpisodes: > 0 } ? series.TotalEpisodes : null);
        var existing = series is null ? new Dictionary<int, EpisodeRow>() : episodes[series.Id].ToDictionary(e => e.Number);

        var plans = new List<LibraryEpisodePlan>();
        var files = g.Folder?.Files ?? [];
        var numbers = files.Select(f => CosKeys.EpisodeNumber(f.Name)).Where(n => n is not null).GroupBy(n => n!.Value)
            .Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet();
        foreach (var f in files)
        {
            var number = CosKeys.EpisodeNumber(f.Name);
            if (number is null)
            {
                plans.Add(new(0, f.Name, f.Bytes, "unnumbered", null, "文件名里没有集数（应为 名称_集数.mp4）"));
                continue;
            }
            existing.TryGetValue(number.Value, out var row);
            plans.Add(Decide(number.Value, f, row));
        }

        LibraryEpisodePlan Decide(int n, LibraryVideoFile f, EpisodeRow? row)
        {
            if (numbers.Contains(n)) return new(n, f.Name, f.Bytes, "duplicate", row?.Id, $"有多个第 {n} 集的文件");
            if (removed) return new(n, f.Name, f.Bytes, "held", row?.Id, null);
            if (row is { Status: EpisodeStatus.Ready or EpisodeStatus.Processing })
            {
                var state = row.Status == EpisodeStatus.Ready ? "uploaded" : "processing";
                // A COS episode knows its object's size: a different size means the file changed after upload.
                if (row.VideoBytes is { } bytes && bytes != f.Bytes && row.Status == EpisodeStatus.Ready)
                    return new(n, f.Name, f.Bytes, "changed", row.Id, "与网站上的文件大小不同；需要时可单独替换");
                return new(n, f.Name, f.Bytes, state, row.Id, null);
            }
            if (!f.Complete) return new(n, f.Name, f.Bytes, "incomplete", row?.Id, "文件不完整（可能还在下载）");
            if (useCos && Unplayable(f) is { } why) return new(n, f.Name, f.Bytes, "unplayable", row?.Id, why);
            if (total is { } t && n > t) return new(n, f.Name, f.Bytes, "held", row?.Id, $"超出总集数 {t}");
            if (row is { Status: EpisodeStatus.Failed }) return new(n, f.Name, f.Bytes, "failed", row.Id, null);
            if (useCos && g.Folder is not null
                && stored.TryGetValue(CosKeys.EpisodeKey(folder, g.Folder.Name, f.Name), out var size) && size == f.Bytes)
                return new(n, f.Name, f.Bytes, "stored", row?.Id, "已在 COS 中，无需再上传");
            return new(n, f.Name, f.Bytes, "new", row?.Id, null);
        }

        plans = [.. plans.OrderBy(p => p.Number == 0 ? int.MaxValue : p.Number).ThenBy(p => p.FileName, StringComparer.Ordinal)];
        var hasCover = series?.CoverKey is not null;
        var toUpload = plans.Count(p => p.State is "new" or "stored" or "failed");
        // A title not yet on the site is created only when nothing about it is unclear; one already there keeps syncing.
        var status = removed || (series is null && problems.Count > 0) ? "attention"
            : series is null ? "new"
            : toUpload > 0 || (!hasCover && g.Image is not null) ? "updates"
            : problems.Count > 0 ? "attention"
            : "synced";

        return new LibraryTitlePlan(
            g.Key, m.Title, m.ExternalId, g.Folder?.Name, g.Image?.Name, g.Details?.Name,
            series?.Id, series?.Status, hasCover, total, status, problems, plans, g.Folder?.Downloading?.Count ?? 0);
    }

    /// <summary>Why a browser could not play this file as it is, or null. Nothing re-encodes a COS video.</summary>
    private static string? Unplayable(LibraryVideoFile f)
    {
        if (!f.Faststart) return "索引不在文件开头（需用 ffmpeg -movflags +faststart 处理），否则要下载完整个文件才能播放";
        if (f.VideoCodec is null || !PlayableVideo.Contains(f.VideoCodec, StringComparer.OrdinalIgnoreCase))
            return $"视频编码为 {f.VideoCodec ?? "未知"}，需要 H.264（avc1）";
        if (f.AudioCodec is not null && !string.Equals(f.AudioCodec, "mp4a", StringComparison.OrdinalIgnoreCase))
            return $"音频编码为 {f.AudioCodec}，需要 AAC（mp4a）";
        return null;
    }

    // ---------- apply one title ----------

    private static async Task<Results<Ok<LibraryApplyResponse>, BadRequest<ApiErrorResponse>, Conflict<ApiErrorResponse>>> Apply(
        LibraryApplyRequest request, HttpContext http, ReelGuoDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (Validate(request.Listing) is { } invalid) return TypedResults.BadRequest(invalid);
        var groups = Group(request.Listing);
        if (groups.Count != 1) return TypedResults.BadRequest(ApiErrorResponse.Of("one_title", "一次只能处理一部剧"));
        var g = groups[0];

        var category = await db.Categories.FirstOrDefaultAsync(c => c.Slug == request.CategorySlug, ct);
        if (category is null) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_category", "请选择频道"));
        var territories = PublishRules.NormalizeTerritories(request.Rights?.Territories ?? []);
        if (territories is null) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_territories", "授权地区需为 ISO 国家代码（如 US、JP）或 *（全球）"));

        var m = Match(g, await CatalogueAsync(db, ct));
        List<string> unclear = m.Series is null ? [.. g.Problems, .. m.Problems] : m.Problems;
        if (unclear.Count > 0) return TypedResults.Conflict(ApiErrorResponse.Of("title_unclear", unclear[0], unclear));
        var now = time.GetUtcNow();
        var p = g.Parsed;
        var rights = request.Rights;

        Series series;
        var created = m.Series is null;
        if (created)
        {
            if (category.Status != CategoryStatus.Active)
                return TypedResults.BadRequest(ApiErrorResponse.Of("category_not_open", $"频道「{category.Name}」未开放，不能添加内容"));
            series = new Series { Title = m.Title, Status = SeriesStatus.Draft, CategoryId = category.Id, CreatedAt = now };
            db.Series.Add(series);
        }
        else
        {
            series = await db.Series.FirstAsync(s => s.Id == m.Series!.Id, ct);
            if (series.Status == SeriesStatus.Removed)
                return TypedResults.Conflict(ApiErrorResponse.Of("series_removed", "该剧已在后台删除，不会再上传"));
        }

        // Only what the series lacks is filled in, so an admin's edits in the editor are never overwritten.
        if (series.ExternalId is null || (series.ExternalId.StartsWith("n-", StringComparison.Ordinal) && p?.Id is not null)) series.ExternalId = m.ExternalId;
        if (series.Description.Length == 0) series.Description = Trim(p?.SynopsisZh ?? p?.SynopsisEn ?? "", 2000);
        if (series.Tags.Count == 0) series.Tags = [.. (p?.Genres ?? []).Select(t => Trim(t, 20)).Distinct().Take(10)];
        if (series.TotalEpisodes == 0 && p?.Total is { } total) series.TotalEpisodes = Math.Min(total, 10000);
        if (series.RightsHolder is null && Clean(rights?.RightsHolder) is { } holder) series.RightsHolder = Trim(holder, 200);
        if (series.ContractRef is null && Clean(rights?.ContractRef) is { } contract) series.ContractRef = Trim(contract, 200);
        if (series.LicenceStart is null && rights?.LicenceStart is { } start) series.LicenceStart = start;
        if (series.Territories.Count == 0 && territories.Count > 0) series.Territories = territories;
        if (series.AiGenerated is null && rights?.AiGenerated is { } ai) series.AiGenerated = ai;
        if (series.HasRealPerson is null && rights?.HasRealPerson is { } person) series.HasRealPerson = person;
        series.UpdatedAt = now;

        db.AddAudit(now, AdminActor.From(http), created ? "library.series_create" : "library.series_update", "series",
            created ? m.ExternalId : series.Id.ToString(), new { series.Title, folder = g.Folder?.Name });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return TypedResults.Conflict(ApiErrorResponse.Of("external_id_taken", "这部剧刚被另一个页面创建，请重新检查文件夹"));
        }
        return TypedResults.Ok(new LibraryApplyResponse(series.Id, created, series.Title));
    }

    // ---------- one episode ----------

    private static async Task<Results<Ok<LibraryEpisodeTicket>, BadRequest<ApiErrorResponse>, NotFound<ApiErrorResponse>, Conflict<ApiErrorResponse>>> Start(
        LibraryEpisodeStartRequest request, HttpContext http, ReelGuoDbContext db, StorageBuckets storage, CosVideoStore cos,
        TimeProvider time, CancellationToken ct)
    {
        var series = await db.Series.FirstOrDefaultAsync(s => s.Id == request.SeriesId, ct);
        if (series is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "剧集不存在"));
        if (series.Status == SeriesStatus.Removed) return TypedResults.Conflict(ApiErrorResponse.Of("series_removed", "该剧已在后台删除"));
        if (request.Number is < 1 or > 100000) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_episode_number", "集数无效"));
        if (string.IsNullOrWhiteSpace(request.FileName) || string.IsNullOrWhiteSpace(request.TitleFolder))
            return TypedResults.BadRequest(ApiErrorResponse.Of("file_required", "缺少文件名"));
        if (request.Bytes <= 0) return TypedResults.BadRequest(ApiErrorResponse.Of("empty_file", "文件为空"));
        var folder = StorageFolder(request.StorageFolder, cos);
        if (folder is null) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_folder", "存储文件夹名称无效"));

        var episode = await db.Episodes.FirstOrDefaultAsync(e => e.SeriesId == series.Id && e.EpisodeNumber == request.Number, ct);
        if (episode is { Status: EpisodeStatus.Ready or EpisodeStatus.Processing } && !request.Replace)
            return TypedResults.Conflict(ApiErrorResponse.Of("episode_uploaded", $"第 {request.Number} 集已上传"));

        var now = time.GetUtcNow();
        string key;
        var stored = false;
        UploadTicket? ticket;
        if (cos.Enabled)
        {
            (key, stored) = await CosUploadKeys.ResolveAsync(db, cos, episode, series.Id, request.Number, folder,
                request.TitleFolder, request.FileName, linkBytes: request.Bytes, ct);
            ticket = stored ? null : CosUploadKeys.Ticket(cos, key, request.ContentType);
        }
        else
        {
            key = AdminCatalogEndpoints.SourceKeyFor(series.Id, request.FileName);
            ticket = AdminCatalogEndpoints.TicketFor(storage, key, request.ContentType);
        }

        if (episode is null)
        {
            episode = new Episode { SeriesId = series.Id, EpisodeNumber = request.Number, Title = "", CreatedAt = now };
            db.Episodes.Add(episode);
        }
        // A ready episode keeps playing its current video until the new one is confirmed and processed.
        episode.SourceKey = key;
        if (episode.Status != EpisodeStatus.Ready) episode.Status = EpisodeStatus.Uploading;
        episode.ProcessingError = null;
        episode.UpdatedAt = now;
        db.AddAudit(now, AdminActor.From(http), "library.episode_upload", "series", series.Id.ToString(),
            new { episode = request.Number, key, stored });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return TypedResults.Conflict(ApiErrorResponse.Of("episode_busy", $"第 {request.Number} 集正在另一个页面上传"));
        }
        return TypedResults.Ok(new LibraryEpisodeTicket(episode.Id, stored, ticket));
    }

    private static async Task<Results<Ok<AdminEpisodeDto>, BadRequest<ApiErrorResponse>, NotFound<ApiErrorResponse>>> Commit(
        long id, LibraryCommitRequest request, HttpContext http, ReelGuoDbContext db, StorageBuckets storage, CosVideoStore cos,
        MediaJobQueue queue, MediaUrlSigner signer, TimeProvider time, CancellationToken ct)
    {
        var episode = await db.Episodes.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (episode is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "分集不存在"));
        if (episode.SourceKey is null) return TypedResults.BadRequest(ApiErrorResponse.Of("source_missing", "该分集还没有上传视频"));

        if (CosKeys.IsCosKey(episode.SourceKey))
        {
            if (!cos.Enabled) return TypedResults.BadRequest(ApiErrorResponse.Of("cos_disabled", "视频存储未设置为腾讯云 COS"));
            var size = await cos.SizeAsync(episode.SourceKey, ct);
            if (size is null) return TypedResults.BadRequest(ApiErrorResponse.Of("source_missing", "COS 中没有找到上传的文件，请重新上传"));
            // The size is how an upload is confirmed to be the file the browser said it sent.
            if (request.Bytes > 0 && size != request.Bytes)
                return TypedResults.BadRequest(ApiErrorResponse.Of("size_mismatch", "COS 中的文件大小与本地文件不一致，请重新上传"));
        }
        else if (!await storage.Raw.ExistsAsync(episode.SourceKey, ct))
        {
            return TypedResults.BadRequest(ApiErrorResponse.Of("source_missing", "没有找到上传的视频文件，请重新上传"));
        }

        if (episode.Status != EpisodeStatus.Ready) episode.Status = EpisodeStatus.Processing;
        episode.ProcessingProgress = 0;
        episode.ProcessingError = null;
        episode.UpdatedAt = time.GetUtcNow();
        await queue.EnqueueTranscodeAsync(id, ct);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(AdminCatalogEndpoints.ToDto(episode, signer));
    }

    private static async Task<Results<NoContent, NotFound<ApiErrorResponse>>> Abort(
        long id, ReelGuoDbContext db, TimeProvider time, CancellationToken ct)
    {
        var episode = await db.Episodes.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (episode is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "分集不存在"));
        if (episode.Status == EpisodeStatus.Uploading)
        {
            episode.ProcessingError = "上传未完成，请重新上传";
            episode.UpdatedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        return TypedResults.NoContent();
    }

    // ---------- grouping and matching ----------

    private static string? StorageFolder(string? requested, CosVideoStore cos) =>
        string.IsNullOrWhiteSpace(requested) ? cos.DefaultFolder : CosKeys.FolderName(requested);

    private static string TitlePrefix(string folder, string titleFolder) =>
        $"{CosKeys.VideoRoot}{CosKeys.Segment(folder)}/{CosKeys.Segment(titleFolder)}/";

    private static ApiErrorResponse? Validate(LibraryListing? listing)
    {
        if (listing is null) return ApiErrorResponse.Of("invalid_listing", "没有文件列表");
        var folders = listing.Folders ?? [];
        if (folders.Count > 5000 || (listing.Images?.Count ?? 0) > 5000 || (listing.Details?.Count ?? 0) > 5000
            || folders.Sum(f => f.Files?.Count ?? 0) > 100000)
            return ApiErrorResponse.Of("listing_too_large", "文件太多，请分批选择文件夹");
        if ((listing.Details ?? []).Any(d => (d.Text?.Length ?? 0) > 65536))
            return ApiErrorResponse.Of("details_too_large", "详情文件不能超过 64 KB");
        var names = folders.Select(f => f.Name).Concat(folders.SelectMany(f => f.Files ?? []).Select(f => f.Name))
            .Concat((listing.Images ?? []).Select(i => i.Name)).Concat((listing.Details ?? []).Select(d => d.Name));
        if (names.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 300))
            return ApiErrorResponse.Of("invalid_name", "文件名为空或过长");
        return null;
    }

    internal sealed record Details(
        string? Id, string? TitleZh, string? TitleEn, string? SynopsisZh, string? SynopsisEn, string[] Genres, int? Total);

    /// <summary>One title as the listing presents it, gathered from the three folders by name key.</summary>
    internal sealed class Grouped(string key)
    {
        public string Key { get; } = key;
        public LibraryFolder? Folder { get; set; }
        public LibraryImage? Image { get; set; }
        public LibraryDetails? Details { get; set; }
        public Details? Parsed { get; set; }
        public List<string> Problems { get; } = [];
    }

    internal static List<Grouped> Group(LibraryListing listing)
    {
        var groups = new Dictionary<string, Grouped>(StringComparer.Ordinal);
        Grouped? Get(string name, bool create)
        {
            var key = CosKeys.NameKey(name);
            if (key.Length == 0) return null;
            if (groups.TryGetValue(key, out var g)) return g;
            if (!create) return null;
            return groups[key] = new Grouped(key);
        }

        foreach (var f in listing.Folders ?? [])
        {
            if (Get(f.Name, true) is not { } g) continue;
            if (g.Folder is null) g.Folder = f;
            else g.Problems.Add($"有多个与「{f.Name}」同名的视频文件夹");
        }
        foreach (var i in listing.Images ?? [])
        {
            if (Get(Path.GetFileNameWithoutExtension(i.Name), true) is not { } g) continue;
            // Several pictures for one title: the tall one is the cover.
            if (g.Image is null || (i.Height > i.Width && g.Image.Height <= g.Image.Width)) g.Image = i;
        }
        foreach (var d in listing.Details ?? [])
        {
            var parsed = Parse(d.Text);
            var stem = Path.GetFileNameWithoutExtension(d.Name);
            // The file name, then the titles inside it: the folder may be named after either.
            var g = new[] { stem, parsed?.TitleEn, parsed?.TitleZh }.OfType<string>().Select(n => Get(n, false)).FirstOrDefault(x => x is not null)
                ?? Get(stem, true);
            if (g is null) continue;
            if (g.Details is not null) { g.Problems.Add($"有多个「{g.Key}」的详情文件"); continue; }
            g.Details = d;
            g.Parsed = parsed;
            if (parsed is null) g.Problems.Add($"详情文件 {d.Name} 不是有效的 JSON");
        }
        return [.. groups.Values];
    }

    /// <summary>A reelpico.series/1 details file, tolerant of a BOM; null when it is not JSON.</summary>
    internal static Details? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        JsonNode? root;
        try { root = JsonNode.Parse(text.Trim().TrimStart('﻿')); }
        catch (JsonException) { return null; }
        if (root is not JsonObject o) return null;

        static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim()
            : n is JsonValue num && num.TryGetValue<long>(out var l) ? l.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        static string[] List(JsonNode? n) => n switch
        {
            JsonArray a => [.. a.Select(Str).OfType<string>()],
            JsonValue => Str(n) is { } one ? [one] : [],
            _ => [],
        };
        var id = Str(o["id"]);
        return new Details(
            id is { Length: > 0 and <= 128 } ? id : null,
            Str(o["title"]?["zh"]), Str(o["title"]?["en"]), Str(o["synopsis"]?["zh"]), Str(o["synopsis"]?["en"]),
            List(o["genres"]),
            o["total_episodes"] is JsonValue t && t.TryGetValue<int>(out var total) && total > 0 ? total : null);
    }

    private sealed record SeriesRow(long Id, string Title, string? ExternalId, SeriesStatus Status, string? CoverKey, int TotalEpisodes);

    private sealed record Catalogue(Dictionary<string, SeriesRow> ByExternalId, ILookup<string, SeriesRow> ByNameKey);

    private static async Task<Catalogue> CatalogueAsync(ReelGuoDbContext db, CancellationToken ct)
    {
        var rows = await db.Series.AsNoTracking()
            .Select(s => new SeriesRow(s.Id, s.Title, s.ExternalId, s.Status, s.CoverKey, s.TotalEpisodes)).ToListAsync(ct);
        return new Catalogue(
            rows.Where(r => r.ExternalId is not null).ToDictionary(r => r.ExternalId!, StringComparer.Ordinal),
            rows.ToLookup(r => CosKeys.NameKey(r.Title), StringComparer.Ordinal));
    }

    private sealed record Matched(SeriesRow? Series, string ExternalId, string Title, List<string> Problems);

    /// <summary>
    /// The series a title belongs to: by the details file's id, else by the id derived from its name (a title that
    /// arrived without details first), else by exactly one series of the same name. Several never get guessed.
    /// </summary>
    private static Matched Match(Grouped g, Catalogue c)
    {
        var p = g.Parsed;
        var derived = CosKeys.DerivedId(g.Key);
        var externalId = p?.Id ?? derived;
        var title = Trim(Clean(p?.TitleZh) ?? Clean(p?.TitleEn) ?? DisplayName(g), 200);
        var problems = new List<string>();

        if (c.ByExternalId.TryGetValue(externalId, out var byId)) return new(byId, externalId, title, problems);
        if (p?.Id is not null && c.ByExternalId.TryGetValue(derived, out var byDerived)) return new(byDerived, externalId, title, problems);

        var names = new[] { p?.TitleZh, p?.TitleEn }.OfType<string>().Select(CosKeys.NameKey).Append(g.Key).Distinct();
        var sameName = names.SelectMany(n => c.ByNameKey[n]).DistinctBy(s => s.Id).ToList();
        // A series another source imported under its own id is not this delivery's; only an unlinked one is adopted.
        var candidates = sameName.Where(s => s.ExternalId is null || s.ExternalId.StartsWith("n-", StringComparison.Ordinal)).ToList();
        if (candidates.Count == 1) return new(candidates[0], externalId, title, problems);
        if (candidates.Count > 1) problems.Add($"后台有 {candidates.Count} 部同名的剧，无法确定是哪一部");
        else if (sameName.Count > 0) problems.Add("后台已有同名的剧（来自其他导入），请确认后再上传");
        return new(null, externalId, title, problems);
    }

    /// <summary>The title when there are no details: the folder name (or picture), without a leading "(Dubbed)".</summary>
    private static string DisplayName(Grouped g)
    {
        var name = g.Folder?.Name ?? (g.Image is { } i ? Path.GetFileNameWithoutExtension(i.Name) : null)
            ?? (g.Details is { } d ? Path.GetFileNameWithoutExtension(d.Name) : g.Key);
        var s = System.Text.RegularExpressions.Regex.Replace(name.Normalize(NormalizationForm.FormC).Trim(), @"^\(\s*dubbed\s*\)\s*", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return s.Length == 0 ? g.Key : s;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>Where a new upload goes in the COS bucket, shared by the folder upload and the episode editor.</summary>
internal static class CosUploadKeys
{
    public static UploadTicket Ticket(CosVideoStore cos, string key, string? contentType)
    {
        var type = string.IsNullOrWhiteSpace(contentType) ? StorageKeys.ContentTypeFor(key) : contentType;
        return new UploadTicket("PUT", cos.UploadUrl(key), new() { ["Content-Type"] = type }, key);
    }

    /// <summary>
    /// The episode's key, "videos/{folder}/{title folder}/{file}", and whether that object is already there.
    /// Never the object the episode plays now, never another episode's, and never an object nobody here uploaded
    /// (another site sharing the bucket, a hand upload) unless it is the very file being sent: same name, same size,
    /// given in <paramref name="linkBytes"/>, which is then linked instead of sent again. Taken names get
    /// "~{series}-{episode}" before the extension.
    /// </summary>
    public static async Task<(string Key, bool Stored)> ResolveAsync(
        ReelGuoDbContext db, CosVideoStore cos, Episode? episode, long seriesId, int number,
        string folder, string titleFolder, string fileName, long? linkBytes, CancellationToken ct)
    {
        var episodeId = episode?.Id ?? 0;
        var suffix = $"{seriesId}-{number}";
        string[] candidates =
        [
            CosKeys.EpisodeKey(folder, titleFolder, fileName),
            CosKeys.EpisodeKey(folder, titleFolder, fileName, suffix),
            CosKeys.EpisodeKey(folder, titleFolder, fileName, $"{suffix}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3))}"),
        ];
        foreach (var key in candidates)
        {
            if (key == episode?.VideoKey) continue;
            if (await db.Episodes.AnyAsync(e => e.Id != episodeId && (e.SourceKey == key || e.VideoKey == key), ct)) continue;
            var size = await cos.SizeAsync(key, ct);
            if (size is null) return (key, false);
            if (linkBytes is not null && size == linkBytes) return (key, true);
            if (key == episode?.SourceKey) return (key, false); // this episode's own earlier upload, never confirmed
        }
        return (candidates[^1], false);
    }
}
