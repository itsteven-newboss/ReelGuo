using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Data;
using ReelGuo.Data.Catalog;
using ReelGuo.Data.Entities;
using ReelGuo.Data.Media;

namespace ReelGuo.Api.Admin;

/// <param name="Chosen">Times viewers took this option.</param>
/// <param name="TimedOut">Of those, times the countdown took it for them.</param>
/// <param name="Effects">How taking it changes the hidden scores.</param>
/// <param name="Conditions">It shows only when all of these hold.</param>
public sealed record AdminStoryOptionDto(
    long Id, string Label, string? Description, long TargetEpisodeId, bool IsDefault, long Chosen, long TimedOut,
    List<StoryEffect> Effects, List<StoryCondition> Conditions);

/// <param name="Price">Coins to unlock the scene (0 = free): its own <c>custom_price</c>, else the series' pricing.</param>
/// <param name="CustomPrice">The scene's own price; null = the series' rule.</param>
/// <param name="ViewersHere">Signed-in viewers whose saved place is this scene.</param>
/// <param name="EndingReached">Ending scenes: signed-in viewers who reached it.</param>
/// <param name="Unlocks">Viewers who paid to unlock this scene.</param>
public sealed record AdminSceneDto(
    long EpisodeId, int EpNum, string Title, EpisodeStatus Status, double Duration, string? PosterUrl, bool IsFree, int Price,
    SceneOutcome Outcome, string? Prompt, int LeadSeconds, int? Countdown, long? NextEpisodeId, string? EndingTitle,
    List<AdminStoryOptionDto> Options, int ViewersHere, int EndingReached, int Unlocks, int? CustomPrice = null);

/// <summary>Where the admin placed a scene on the story map.</summary>
public sealed record AdminMapPoint(long EpisodeId, int X, int Y);

/// <param name="StartEpisodeId">The chosen start scene (null = the lowest-numbered scene).</param>
/// <param name="EffectiveStartEpisodeId">The scene the story actually starts with.</param>
/// <param name="Variables">The hidden scores (好感度 …).</param>
/// <param name="Layout">Scenes the admin placed on the map; the others are laid out automatically.</param>
public sealed record AdminStoryDto(
    long SeriesId, string Title, SeriesKind Kind, SeriesStatus Status, long? StartEpisodeId, long? EffectiveStartEpisodeId,
    List<AdminSceneDto> Scenes, List<StoryProblem> Problems, List<StoryVariable> Variables, List<AdminMapPoint> Layout);

/// <param name="Id">An existing option's id (keeps its statistics); null for a new option.</param>
public sealed record AdminStoryOptionInput(
    long? Id, string? Label, string? Description, long TargetEpisodeId, bool IsDefault,
    List<StoryEffect>? Effects = null, List<StoryCondition>? Conditions = null);

/// <summary>What happens when one scene ends. Only the fields of the chosen <c>outcome</c> are kept.</summary>
/// <param name="IsFree">始终免费; left out, it is kept as it is.</param>
/// <param name="CustomPrice">The scene's own price in coins (0-10000); null = the series' rule.</param>
public sealed record AdminSceneInput(
    long EpisodeId, SceneOutcome Outcome, string? Prompt, int LeadSeconds, int? Countdown, long? NextEpisodeId, string? EndingTitle,
    List<AdminStoryOptionInput>? Options, bool? IsFree = null, int? CustomPrice = null);

/// <param name="Scenes">The scenes to set up; scenes left out keep their settings.</param>
/// <param name="Variables">The hidden scores; left out, they are kept as they are.</param>
/// <param name="Layout">Scene positions on the map; left out, kept; an empty list returns to the automatic layout.</param>
public sealed record AdminStoryInput(long? StartEpisodeId, List<AdminSceneInput> Scenes, List<StoryVariable>? Variables = null, List<AdminMapPoint>? Layout = null);

/// <summary>The story editor (剧情编辑) of an interactive series.</summary>
public static class AdminStoryEndpoints
{
    public static RouteGroupBuilder MapAdminStory(this RouteGroupBuilder series)
    {
        series.MapGet("/{id:long}/story", GetStory)
            .WithSummary("An interactive series' scenes with what happens when each ends, the problems found, and per-option statistics.");
        series.MapPut("/{id:long}/story", SaveStory)
            .WithSummary("Saves the start scene and the scenes' settings at once. A published series' story must stay playable: a save with errors is refused with the problems listed.");
        return series;
    }

    private static async Task<Results<Ok<AdminStoryDto>, NotFound<ApiErrorResponse>>> GetStory(
        long id, ReelGuoDbContext db, MediaUrlSigner signer, CancellationToken ct)
    {
        var dto = await LoadAsync(db, signer, id, ct);
        return dto is null ? TypedResults.NotFound(ApiErrorResponse.Of("not_found", "剧集不存在")) : TypedResults.Ok(dto);
    }

    internal static async Task<AdminStoryDto?> LoadAsync(ReelGuoDbContext db, MediaUrlSigner signer, long id, CancellationToken ct)
    {
        var series = await db.Series.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (series is null) return null;
        var scenes = await db.Episodes.AsNoTracking().Where(e => e.SeriesId == id).Include(e => e.Options)
            .OrderBy(e => e.EpisodeNumber).ToListAsync(ct);

        var here = await db.StoryProgress.Where(p => p.SeriesId == id).GroupBy(p => p.Scene)
            .Select(g => new { Scene = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Scene, x => x.Count, ct);
        var unlocks = await db.EpisodeUnlocks.Where(u => u.SeriesId == id).GroupBy(u => u.EpisodeNumber)
            .Select(g => new { Ep = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Ep, x => x.Count, ct);
        var reached = new Dictionary<int, int>();
        foreach (var ending in scenes.Where(e => e.Outcome == SceneOutcome.Ending))
            reached[ending.EpisodeNumber] = await db.StoryProgress.CountAsync(p => p.SeriesId == id && p.Endings.Contains(ending.EpisodeNumber), ct);

        var specs = scenes.Select(StoryRules.ToSpec).ToList();
        var dtos = scenes.Select(e => new AdminSceneDto(
            e.Id, e.EpisodeNumber, e.Title, e.Status, e.DurationSeconds, signer.PublicUrlOrNull(e.PosterKey), e.IsFree,
            Pricing.EpisodePrice(true, series.EpisodePrice, series.FreeEpisodes, e.EpisodeNumber, e.IsFree, e.CustomPrice),
            e.Outcome, e.ChoicePrompt, e.ChoiceLeadSeconds, e.ChoiceCountdown, e.NextEpisodeId, e.EndingTitle,
            e.Options.OrderBy(o => o.SortOrder)
                .Select(o => new AdminStoryOptionDto(o.Id, o.Label, o.Description, o.TargetEpisodeId, o.IsDefault, o.ChosenCount, o.TimeoutCount,
                    StoryJson.List<StoryEffect>(o.EffectsJson), StoryJson.List<StoryCondition>(o.ConditionsJson))).ToList(),
            here.GetValueOrDefault(e.EpisodeNumber), reached.GetValueOrDefault(e.EpisodeNumber), unlocks.GetValueOrDefault(e.EpisodeNumber),
            e.CustomPrice)).ToList();

        var ids = scenes.Select(e => e.Id).ToHashSet();
        var layout = StoryJson.Layout(series.StoryLayoutJson).Where(p => ids.Contains(p.Key))
            .Select(p => new AdminMapPoint(p.Key, p.Value.X, p.Value.Y)).ToList();
        return new AdminStoryDto(series.Id, series.Title, series.Kind, series.Status, series.StoryStartEpisodeId,
            StoryRules.StartOf(specs, series.StoryStartEpisodeId), dtos, StoryRules.Check(specs, series.StoryStartEpisodeId),
            StoryJson.List<StoryVariable>(series.StoryVariablesJson), layout);
    }

    /// <summary>Checks the hidden scores; returns why they can't be saved, or null.</summary>
    private static string? CheckVariables(List<StoryVariable> variables)
    {
        if (variables.Count > StoryRules.MaxVariables) return $"最多 {StoryRules.MaxVariables} 个隐藏数值";
        if (variables.Any(v => string.IsNullOrWhiteSpace(v.Key) || v.Key.Length > 16)) return "隐藏数值缺少编号";
        if (variables.Select(v => v.Key).Distinct().Count() != variables.Count) return "隐藏数值的编号重复";
        if (variables.Any(v => (v.Name ?? "").Trim().Length is 0 or > StoryRules.MaxVariableName)) return $"隐藏数值的名称需为 1-{StoryRules.MaxVariableName} 个字";
        if (variables.Select(v => v.Name.Trim()).Distinct().Count() != variables.Count) return "隐藏数值的名称不能重复";
        if (variables.Any(v => v.Initial is < -StoryRules.MaxScore or > StoryRules.MaxScore)) return $"隐藏数值的初始值需在 ±{StoryRules.MaxScore} 之间";
        return null;
    }

    /// <summary>Checks an option's effects and conditions against the scores; returns why they can't be saved, or null.</summary>
    private static string? CheckLogic(string name, AdminStoryOptionInput option, HashSet<string> keys)
    {
        var effects = option.Effects ?? [];
        var conditions = option.Conditions ?? [];
        var label = $"{name} 的选项「{option.Label}」";
        if (effects.Count > StoryRules.MaxEffects) return $"{label}最多改变 {StoryRules.MaxEffects} 个数值";
        if (conditions.Count > StoryRules.MaxConditions) return $"{label}最多 {StoryRules.MaxConditions} 个显示条件";
        if (effects.Any(e => !keys.Contains(e.Variable)) || conditions.Any(c => !keys.Contains(c.Variable))) return $"{label}用到了不存在的隐藏数值";
        if (effects.Any(e => e.Add == 0 || Math.Abs(e.Add) > StoryRules.MaxScore)) return $"{label}的数值变化需为 ±1 到 ±{StoryRules.MaxScore}";
        if (conditions.Any(c => !StoryCondition.Ops.Contains(c.Op) || Math.Abs(c.Value) > StoryRules.MaxScore)) return $"{label}的显示条件无效";
        return null;
    }

    private static async Task<Results<Ok<AdminStoryDto>, NotFound<ApiErrorResponse>, BadRequest<ApiErrorResponse>>> SaveStory(
        long id, AdminStoryInput input, HttpContext http, ReelGuoDbContext db, MediaUrlSigner signer, TimeProvider time, CancellationToken ct)
    {
        static BadRequest<ApiErrorResponse> Bad(string code, string message) => TypedResults.BadRequest(ApiErrorResponse.Of(code, message));

        var series = await db.Series.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (series is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "剧集不存在"));
        if (series.Kind != SeriesKind.Interactive) return Bad("not_interactive", "这部剧不是互动剧，先在剧集资料里把类型改为互动剧");

        var scenes = await db.Episodes.Where(e => e.SeriesId == id).Include(e => e.Options).ToListAsync(ct);
        var byId = scenes.ToDictionary(e => e.Id);
        if (input.StartEpisodeId is { } start && !byId.ContainsKey(start)) return Bad("invalid_start", "起点片段不属于这部剧");
        var variables = input.Variables?.Select(v => v with { Name = (v.Name ?? "").Trim() }).ToList() ?? StoryJson.List<StoryVariable>(series.StoryVariablesJson);
        if (CheckVariables(variables) is { } badVariables) return Bad("invalid_variables", badVariables);
        var keys = variables.Select(v => v.Key).ToHashSet();
        if ((input.Layout ?? []).Any(p => !byId.ContainsKey(p.EpisodeId) || p.X is < -2000 or > 50000 || p.Y is < -2000 or > 50000))
            return Bad("invalid_layout", "剧情图的位置无效");

        // Shape checks first, so nothing is half-applied.
        foreach (var scene in input.Scenes ?? [])
        {
            if (!byId.TryGetValue(scene.EpisodeId, out var episode)) return Bad("invalid_scene", "片段不属于这部剧");
            var name = $"S{episode.EpisodeNumber}";
            if ((scene.Prompt ?? "").Trim().Length > StoryRules.MaxPrompt) return Bad("invalid_prompt", $"{name} 的选择提示不能超过 {StoryRules.MaxPrompt} 个字");
            if (scene.LeadSeconds is < 0 or > StoryRules.MaxLeadSeconds) return Bad("invalid_lead", $"{name} 的选项最多提前 {StoryRules.MaxLeadSeconds} 秒出现");
            if (scene.Countdown is < StoryRules.MinCountdown or > StoryRules.MaxCountdown)
                return Bad("invalid_countdown", $"{name} 的倒计时需在 {StoryRules.MinCountdown}-{StoryRules.MaxCountdown} 秒之间");
            if ((scene.EndingTitle ?? "").Trim().Length > StoryRules.MaxEndingTitle) return Bad("invalid_ending_title", $"{name} 的结局名称不能超过 {StoryRules.MaxEndingTitle} 个字");
            if (scene.NextEpisodeId is { } next && !byId.ContainsKey(next)) return Bad("invalid_target", $"{name} 接着播放的片段不属于这部剧");
            if (scene.CustomPrice is < 0 or > Pricing.MaxEpisodePrice) return Bad("invalid_price", $"{name} 的价格需在 0-{Pricing.MaxEpisodePrice} 金币之间");
            var options = scene.Options ?? [];
            if (options.Count > StoryRules.MaxOptions) return Bad("too_many_options", $"{name} 最多 {StoryRules.MaxOptions} 个选项");
            foreach (var option in options)
            {
                if ((option.Label ?? "").Trim().Length > StoryRules.MaxLabel) return Bad("invalid_label", $"{name} 的选项按钮文字不能超过 {StoryRules.MaxLabel} 个字");
                if ((option.Description ?? "").Trim().Length > StoryRules.MaxDescription) return Bad("invalid_description", $"{name} 的选项说明不能超过 {StoryRules.MaxDescription} 个字");
                if (!byId.ContainsKey(option.TargetEpisodeId)) return Bad("invalid_target", $"{name} 的选项「{option.Label}」需要选择跳转到的片段");
                if (CheckLogic(name, option, keys) is { } badLogic) return Bad("invalid_option_logic", badLogic);
            }
        }
        // Scenes left out keep their options: their effects and conditions must still name existing scores.
        var listed = (input.Scenes ?? []).Select(s => s.EpisodeId).ToHashSet();
        var dangling = scenes.Where(e => !listed.Contains(e.Id)).SelectMany(e => e.Options)
            .Any(o => StoryJson.List<StoryEffect>(o.EffectsJson).Any(x => !keys.Contains(x.Variable))
                || StoryJson.List<StoryCondition>(o.ConditionsJson).Any(x => !keys.Contains(x.Variable)));
        if (dangling) return Bad("variable_in_use", "要删除的隐藏数值还在被选项使用，请先去掉这些选项的数值变化或显示条件");

        foreach (var scene in input.Scenes ?? [])
        {
            var episode = byId[scene.EpisodeId];
            var choice = scene.Outcome == SceneOutcome.Choice;
            episode.Outcome = scene.Outcome;
            episode.ChoicePrompt = choice ? Clean(scene.Prompt) : null;
            episode.ChoiceLeadSeconds = choice ? scene.LeadSeconds : 0;
            episode.ChoiceCountdown = choice ? scene.Countdown : null;
            episode.NextEpisodeId = scene.Outcome == SceneOutcome.Continue ? scene.NextEpisodeId : null;
            episode.EndingTitle = scene.Outcome == SceneOutcome.Ending ? Clean(scene.EndingTitle) : null;
            if (scene.IsFree is { } isFree) episode.IsFree = isFree;
            episode.CustomPrice = scene.CustomPrice;

            // Options keep their id (and statistics) when the editor sends it back.
            var wanted = choice ? scene.Options ?? [] : [];
            var keep = wanted.Where(o => o.Id is not null).Select(o => o.Id!.Value).ToHashSet();
            foreach (var gone in episode.Options.Where(o => !keep.Contains(o.Id)).ToList())
            {
                episode.Options.Remove(gone);
                db.StoryOptions.Remove(gone);
            }
            for (var i = 0; i < wanted.Count; i++)
            {
                var input_ = wanted[i];
                var option = input_.Id is { } oid ? episode.Options.FirstOrDefault(o => o.Id == oid) : null;
                if (option is null)
                {
                    option = new StoryOption { SeriesId = id, FromEpisodeId = episode.Id };
                    episode.Options.Add(option);
                }
                option.SortOrder = i;
                option.Label = (input_.Label ?? "").Trim();
                option.Description = Clean(input_.Description);
                option.TargetEpisodeId = input_.TargetEpisodeId;
                option.IsDefault = input_.IsDefault;
                option.EffectsJson = StoryJson.Write(input_.Effects ?? []);
                option.ConditionsJson = StoryJson.Write(input_.Conditions ?? []);
            }
            episode.UpdatedAt = time.GetUtcNow();
        }
        series.StoryStartEpisodeId = input.StartEpisodeId;
        series.StoryVariablesJson = StoryJson.Write(variables);
        if (input.Layout is { } layout)
            series.StoryLayoutJson = layout.Count == 0 ? null : StoryJson.Write(layout.ToDictionary(p => p.EpisodeId, p => new MapPoint(p.X, p.Y)));
        series.UpdatedAt = time.GetUtcNow();

        // Viewers are playing a published story: it has to stay complete.
        var problems = StoryRules.Check(scenes.Select(StoryRules.ToSpec).ToList(), series.StoryStartEpisodeId);
        var errors = problems.Where(p => p.Level == StoryProblemLevel.Error).Select(p => p.Message).ToList();
        if (series.Status == SeriesStatus.Published && errors.Count > 0)
            return TypedResults.BadRequest(ApiErrorResponse.Of("story_invalid", "剧集已上架，剧情需要完整才能保存，请先处理以下问题", errors));

        db.AddAudit(time, AdminActor.From(http), "series.story", "series", id.ToString(),
            new { start = series.StoryStartEpisodeId, scenes = (input.Scenes ?? []).Count });
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok((await LoadAsync(db, signer, id, ct))!);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Why a scene can't be deleted yet: another scene's option or continuation leads to it.</summary>
    internal static async Task<string?> InUseAsync(ReelGuoDbContext db, long episodeId, CancellationToken ct)
    {
        var option = await db.StoryOptions.Where(o => o.TargetEpisodeId == episodeId && o.FromEpisodeId != episodeId)
            .Select(o => new { o.From.EpisodeNumber, o.Label }).FirstOrDefaultAsync(ct);
        if (option is not null) return $"S{option.EpisodeNumber} 的选项「{option.Label}」会跳到这个片段，请先在剧情编辑里改掉";
        var from = await db.Episodes.Where(e => e.NextEpisodeId == episodeId && e.Id != episodeId).Select(e => (int?)e.EpisodeNumber).FirstOrDefaultAsync(ct);
        return from is null ? null : $"S{from} 播完后会接着播这个片段，请先在剧情编辑里改掉";
    }
}
