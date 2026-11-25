using Microsoft.EntityFrameworkCore;
using ReelGuo.Data.Entities;

namespace ReelGuo.Data.Catalog;

public enum StoryProblemLevel
{
    /// <summary>Blocks publishing, and saving a published story.</summary>
    Error,
    /// <summary>Shown to the admin; doesn't block anything.</summary>
    Warning,
}

/// <param name="EpisodeId">The scene the problem is about, when there is one (the editor links to it).</param>
public sealed record StoryProblem(StoryProblemLevel Level, long? EpisodeId, string Message);

/// <param name="Conditional">The option only shows when its conditions on the hidden scores hold.</param>
public sealed record StoryOptionSpec(string Label, string? Description, long TargetEpisodeId, bool IsDefault, bool Conditional = false);

/// <summary>One scene of an interactive series as the rules see it.</summary>
public sealed record StorySceneSpec(
    long EpisodeId, int Number, bool Ready, SceneOutcome Outcome, string? Prompt, int LeadSeconds, int? Countdown,
    long? NextEpisodeId, string? EndingTitle, IReadOnlyList<StoryOptionSpec> Options);

/// <summary>
/// What an interactive series (互动剧) needs before viewers can play it. Only scenes reachable from the start are held to
/// the rules: spare scenes that nothing leads to only get a warning, so an admin can keep alternates around.
/// Messages are zh-CN for the admin console and name scenes as S1, S2, … (their episode numbers).
/// </summary>
public static class StoryRules
{
    public const int MinOptions = 2;
    public const int MaxOptions = 4;
    public const int MaxLabel = 12;
    public const int MaxDescription = 30;
    public const int MaxPrompt = 40;
    public const int MaxEndingTitle = 20;
    public const int MaxLeadSeconds = 10;
    public const int MinCountdown = 5;
    public const int MaxCountdown = 30;
    public const int MaxVariables = 5;
    public const int MaxVariableName = 8;
    public const int MaxEffects = 3;
    public const int MaxConditions = 2;
    public const int MaxScore = 999;

    /// <summary>The start scene: the chosen one when it still exists, else the lowest-numbered scene.</summary>
    public static long? StartOf(IReadOnlyList<StorySceneSpec> scenes, long? chosen) =>
        chosen is { } id && scenes.Any(s => s.EpisodeId == id) ? id : scenes.OrderBy(s => s.Number).FirstOrDefault()?.EpisodeId;

    /// <summary>Scenes a scene leads to (its options' targets, or the scene it continues to).</summary>
    public static IEnumerable<long> Exits(StorySceneSpec scene) => scene.Outcome switch
    {
        SceneOutcome.Choice => scene.Options.Select(o => o.TargetEpisodeId),
        SceneOutcome.Continue when scene.NextEpisodeId is { } next => [next],
        _ => [],
    };

    public static List<StoryProblem> Check(IReadOnlyList<StorySceneSpec> scenes, long? startEpisodeId)
    {
        var problems = new List<StoryProblem>();
        if (scenes.Count == 0)
        {
            problems.Add(new(StoryProblemLevel.Error, null, "还没有片段，请先上传视频"));
            return problems;
        }

        var byId = scenes.ToDictionary(s => s.EpisodeId);
        var start = StartOf(scenes, startEpisodeId)!.Value;
        var reachable = Reach([start], byId, s => Exits(s));
        string Name(long id) => byId.TryGetValue(id, out var s) ? $"S{s.Number}" : "已删除的片段";
        void Error(StorySceneSpec s, string message) => problems.Add(new(StoryProblemLevel.Error, s.EpisodeId, $"S{s.Number} {message}"));

        foreach (var scene in scenes.Where(s => reachable.Contains(s.EpisodeId)).OrderBy(s => s.Number))
        {
            if (!scene.Ready) Error(scene, "的视频还没处理完成");
            switch (scene.Outcome)
            {
                case SceneOutcome.Unset:
                    Error(scene, "还没设置播完后怎么走");
                    break;
                case SceneOutcome.Choice:
                    if (scene.Options.Count is < MinOptions or > MaxOptions) Error(scene, $"需要 {MinOptions}-{MaxOptions} 个选项");
                    for (var i = 0; i < scene.Options.Count; i++)
                    {
                        var option = scene.Options[i];
                        if (string.IsNullOrWhiteSpace(option.Label)) Error(scene, $"的第 {i + 1} 个选项还没写按钮文字");
                        if (!byId.ContainsKey(option.TargetEpisodeId)) Error(scene, $"的选项「{option.Label}」指向的片段已不存在");
                    }
                    var defaults = scene.Options.Count(o => o.IsDefault);
                    if (scene.Countdown is not null && defaults != 1) Error(scene, "设置了倒计时，需要指定一个默认选项");
                    else if (defaults > 1) Error(scene, "只能有一个默认选项");
                    // The player shows them all rather than none, but the story then ignores the conditions.
                    if (scene.Options.Count > 0 && scene.Options.All(o => o.Conditional))
                        problems.Add(new(StoryProblemLevel.Warning, scene.EpisodeId, $"S{scene.Number} 的选项都有显示条件，条件都不满足时会全部显示，建议留一个没有条件的选项"));
                    break;
                case SceneOutcome.Continue:
                    if (scene.NextEpisodeId is not { } next || !byId.ContainsKey(next)) Error(scene, "还没选择接着播放的片段");
                    else if (next == scene.EpisodeId) Error(scene, "不能接续到自己");
                    break;
                case SceneOutcome.Ending:
                    if (string.IsNullOrWhiteSpace(scene.EndingTitle)) Error(scene, "是结局，需要填写结局名称");
                    break;
            }
        }

        var endings = scenes.Where(s => s.Outcome == SceneOutcome.Ending && reachable.Contains(s.EpisodeId)).Select(s => s.EpisodeId).ToList();
        if (endings.Count == 0)
        {
            problems.Add(new(StoryProblemLevel.Error, null, "从起点走不到任何结局，至少需要一个结局"));
        }
        else
        {
            // Scenes from which no ending can be reached trap the viewer in a loop.
            var entries = scenes.SelectMany(s => Exits(s).Select(to => (From: s.EpisodeId, To: to))).ToLookup(e => e.To, e => e.From);
            var canEnd = Reach(endings, byId, s => entries[s.EpisodeId]);
            foreach (var scene in scenes.Where(s => reachable.Contains(s.EpisodeId) && !canEnd.Contains(s.EpisodeId)).OrderBy(s => s.Number))
                problems.Add(new(StoryProblemLevel.Warning, scene.EpisodeId, $"从 S{scene.Number} 走不到任何结局，观众会一直绕圈"));
        }

        foreach (var scene in scenes.Where(s => !reachable.Contains(s.EpisodeId)).OrderBy(s => s.Number))
            problems.Add(new(StoryProblemLevel.Warning, scene.EpisodeId, $"S{scene.Number} 从起点{Name(start)}走不到，观众看不到它"));

        return problems;
    }

    private static HashSet<long> Reach(IEnumerable<long> from, Dictionary<long, StorySceneSpec> byId, Func<StorySceneSpec, IEnumerable<long>> next)
    {
        var seen = new HashSet<long>();
        var queue = new Queue<long>(from.Where(byId.ContainsKey));
        while (queue.TryDequeue(out var id))
        {
            if (!seen.Add(id)) continue;
            foreach (var to in next(byId[id]))
                if (byId.ContainsKey(to) && !seen.Contains(to)) queue.Enqueue(to);
        }
        return seen;
    }

    public static StorySceneSpec ToSpec(Episode e) => new(
        e.Id, e.EpisodeNumber, e.Status == EpisodeStatus.Ready, e.Outcome, e.ChoicePrompt, e.ChoiceLeadSeconds, e.ChoiceCountdown,
        e.NextEpisodeId, e.EndingTitle,
        e.Options.OrderBy(o => o.SortOrder)
            .Select(o => new StoryOptionSpec(o.Label, o.Description, o.TargetEpisodeId, o.IsDefault, StoryJson.List<StoryCondition>(o.ConditionsJson).Count > 0)).ToList());

    /// <summary>The problems of a series' story as saved.</summary>
    public static async Task<List<StoryProblem>> CheckAsync(ReelGuoDbContext db, long seriesId, CancellationToken ct)
    {
        var start = await db.Series.Where(s => s.Id == seriesId).Select(s => s.StoryStartEpisodeId).FirstOrDefaultAsync(ct);
        var scenes = await db.Episodes.AsNoTracking().Where(e => e.SeriesId == seriesId).Include(e => e.Options).ToListAsync(ct);
        return Check(scenes.Select(ToSpec).ToList(), start);
    }
}
