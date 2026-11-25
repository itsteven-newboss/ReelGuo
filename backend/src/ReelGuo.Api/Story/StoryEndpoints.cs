using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelGuo.Api.Billing;
using ReelGuo.Api.Geo;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Api.Public;
using ReelGuo.Api.Users;
using ReelGuo.Data;
using ReelGuo.Data.Catalog;
using ReelGuo.Data.Entities;

namespace ReelGuo.Api.Story;

/// <param name="Target">Episode number of the scene the option leads to.</param>
/// <param name="Effects">How taking it changes the hidden scores.</param>
/// <param name="Conditions">It shows only when all of these hold for the viewer's scores (computed from their path).</param>
public sealed record StoryOptionDto(long Id, string Label, string? Description, int Target, bool IsDefault, List<StoryEffect> Effects, List<StoryCondition> Conditions);

/// <summary>
/// One scene of an interactive series. For an ending the viewer hasn't reached yet, <c>title</c> is empty and
/// <c>ending_title</c> null, so the page can't give endings away.
/// </summary>
/// <param name="Next">Continue: the episode number that plays next.</param>
/// <param name="Ending">Ending: its number among the endings (结局 2 / 3).</param>
/// <param name="LeadSeconds">Choice: the options appear this many seconds before the end (0 = when it ends).</param>
/// <param name="Countdown">Choice: seconds before the default option plays; null = the options wait.</param>
public sealed record StorySceneDto(
    int EpNum, string Title, double Duration, bool IsFree, int Price, SceneOutcome Outcome, string? Prompt, int LeadSeconds,
    int? Countdown, int? Next, int? Ending, string? EndingTitle, List<StoryOptionDto> Options);

/// <summary>At scene <c>scene</c> (episode number) the viewer took option <c>option</c>.</summary>
public sealed record StoryStepDto(int Scene, long Option);

/// <param name="Endings">Episode numbers of the endings reached.</param>
public sealed record StoryProgressDto(int Scene, List<StoryStepDto> Path, List<int> Endings);

/// <param name="Progress">The signed-in viewer's saved place; null for guests and before the first choice.</param>
/// <param name="Variables">The hidden scores (好感度 …): a viewer's score is its initial value plus the effects of the options on their path.</param>
public sealed record StoryDto(long SeriesId, int Start, int EndingCount, List<StorySceneDto> Scenes, StoryProgressDto? Progress, List<StoryVariable> Variables);

/// <param name="From">Episode number of the scene that just ended.</param>
/// <param name="Option">The option taken; leave out when the scene continues by itself.</param>
/// <param name="TimedOut">The countdown chose the default option.</param>
public sealed record StoryNextRequest(int From, long? Option, bool TimedOut = false);

/// <param name="Scene">Episode number of the scene to play now.</param>
/// <param name="Share">Percent of viewers who took the same option, once enough have chosen; else null.</param>
public sealed record StoryNextDto(int Scene, int? Ending, string? EndingTitle, int? Share);

/// <param name="Endings">Endings reached on this device before signing in (merged with the account's).</param>
public sealed record StoryProgressRequest(int Scene, List<StoryStepDto>? Path, List<int>? Endings);

/// <summary>Interactive series (互动剧): the story, the viewer's next step, and their saved place.</summary>
public static class StoryEndpoints
{
    /// <summary>「64% 的观众也这么选」 shows once this many viewers chose at a choice point.</summary>
    public const int ShareAfter = 100;

    /// <summary>Oldest choices beyond this are dropped (loops can make a path grow without end).</summary>
    public const int MaxPath = 200;

    public static RouteGroupBuilder MapStory(this RouteGroupBuilder v1)
    {
        v1.MapGet("/series/{id:long}/story", GetStory).WithTags("Story")
            .WithSummary("An interactive series' scenes, options and endings, with the signed-in viewer's saved place. 404 when the series isn't interactive or isn't available.");
        v1.MapPost("/series/{id:long}/story/next", Next).WithTags("Story")
            .WithSummary("The scene after the one that ended (the option taken, or the scene it continues to). Counts the choice and, signed in, saves the viewer's place.");
        v1.MapPut("/series/{id:long}/story/progress", SaveProgress).WithTags("Story").RequireAuthorization(UserAuth.Policy)
            .WithSummary("Saves where the viewer is: after going back to an earlier choice, starting over, or signing in with progress made as a guest.");
        return v1;
    }

    private sealed record Loaded(Series Series, List<Episode> Scenes, Dictionary<long, Episode> ById, Dictionary<long, int> EndingNumbers, Episode Start)
    {
        public Episode? ByNumber(int number) => Scenes.FirstOrDefault(e => e.EpisodeNumber == number);
    }

    /// <summary>The series when it is interactive and visible to this visitor, with its playable (ready) scenes.</summary>
    private static async Task<Loaded?> LoadAsync(long id, HttpContext http, ReelGuoDbContext db, ICountryResolver resolver, TimeProvider time, CancellationToken ct)
    {
        var series = await db.Series.AsNoTracking().VisibleTo(resolver.Resolve(http), PublicEndpoints.Today(time))
            .FirstOrDefaultAsync(s => s.Id == id && s.Kind == SeriesKind.Interactive, ct);
        if (series is null) return null;
        var scenes = await db.Episodes.AsNoTracking().Where(e => e.SeriesId == id && e.Status == EpisodeStatus.Ready)
            .Include(e => e.Options).OrderBy(e => e.EpisodeNumber).ToListAsync(ct);
        if (scenes.Count == 0) return null;
        var byId = scenes.ToDictionary(e => e.Id);
        var endings = scenes.Where(e => e.Outcome == SceneOutcome.Ending).Select((e, i) => (e.Id, Number: i + 1)).ToDictionary(x => x.Id, x => x.Number);
        var start = series.StoryStartEpisodeId is { } s && byId.TryGetValue(s, out var chosen) ? chosen : scenes[0];
        return new Loaded(series, scenes, byId, endings, start);
    }

    private static NotFound<ApiErrorResponse> Missing() => TypedResults.NotFound(ApiErrorResponse.Of("not_found", "内容不存在或已下架"));

    private static async Task<Results<Ok<StoryDto>, NotFound<ApiErrorResponse>>> GetStory(
        long id, HttpContext http, ReelGuoDbContext db, ICountryResolver resolver, IOptions<BillingOptions> billing, TimeProvider time, CancellationToken ct)
    {
        var story = await LoadAsync(id, http, db, resolver, time, ct);
        if (story is null) return Missing();

        var uid = await UserAuth.OptionalUserIdAsync(http);
        var saved = uid is { } u ? await db.StoryProgress.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == u && p.SeriesId == id, ct) : null;
        var progress = saved is null ? null : ToDto(saved, story);
        var reached = progress?.Endings.ToHashSet() ?? [];
        var series = story.Series;

        var scenes = story.Scenes.Select(e =>
        {
            var price = Pricing.EpisodePrice(billing.Value.Enabled, series.EpisodePrice, series.FreeEpisodes, e.EpisodeNumber, e.IsFree, e.CustomPrice);
            int? ending = story.EndingNumbers.TryGetValue(e.Id, out var n) ? n : null;
            var hidden = ending is not null && !reached.Contains(e.EpisodeNumber);
            var choice = e.Outcome == SceneOutcome.Choice;
            return new StorySceneDto(
                e.EpisodeNumber, hidden ? "" : e.Title, e.DurationSeconds, price == 0, price, e.Outcome,
                choice ? e.ChoicePrompt : null, e.ChoiceLeadSeconds, choice ? e.ChoiceCountdown : null,
                e.Outcome == SceneOutcome.Continue && e.NextEpisodeId is { } next && story.ById.TryGetValue(next, out var to) ? to.EpisodeNumber : null,
                ending, hidden ? null : e.EndingTitle,
                choice
                    ? e.Options.Where(o => story.ById.ContainsKey(o.TargetEpisodeId)).OrderBy(o => o.SortOrder)
                        .Select(o => new StoryOptionDto(o.Id, o.Label, o.Description, story.ById[o.TargetEpisodeId].EpisodeNumber, o.IsDefault,
                            StoryJson.List<StoryEffect>(o.EffectsJson), StoryJson.List<StoryCondition>(o.ConditionsJson))).ToList()
                    : []);
        }).ToList();

        return TypedResults.Ok(new StoryDto(id, story.Start.EpisodeNumber, story.EndingNumbers.Count, scenes, progress,
            StoryJson.List<StoryVariable>(series.StoryVariablesJson)));
    }

    /// <summary>The saved place, repaired when the story changed under it: steps whose scene or option is gone are dropped,
    /// and a removed current scene falls back to the last choice that still exists, or the start.</summary>
    private static StoryProgressDto ToDto(StoryProgress saved, Loaded story)
    {
        var path = ValidSteps(saved.Path, story);
        var scene = story.ByNumber(saved.Scene)?.EpisodeNumber ?? path.LastOrDefault()?.Scene ?? story.Start.EpisodeNumber;
        var endings = saved.Endings.Where(n => story.ByNumber(n) is { Outcome: SceneOutcome.Ending }).Distinct().Order().ToList();
        return new StoryProgressDto(scene, path.Select(s => new StoryStepDto(s.Scene, s.Option)).ToList(), endings);
    }

    private static List<StoryStep> ValidSteps(IEnumerable<StoryStep> steps, Loaded story) =>
        steps.Where(s => story.ByNumber(s.Scene) is { } scene && scene.Options.Any(o => o.Id == s.Option)).TakeLast(MaxPath).ToList();

    private static async Task<Results<Ok<StoryNextDto>, NotFound<ApiErrorResponse>, BadRequest<ApiErrorResponse>>> Next(
        long id, StoryNextRequest request, HttpContext http, ReelGuoDbContext db, ICountryResolver resolver, WalletService wallets,
        IOptions<BillingOptions> billing, TimeProvider time, CancellationToken ct)
    {
        var story = await LoadAsync(id, http, db, resolver, time, ct);
        if (story is null) return Missing();
        var from = story.ByNumber(request.From);
        if (from is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "片段不存在"));

        StoryOption? option = null;
        Episode? target;
        switch (from.Outcome)
        {
            case SceneOutcome.Choice:
                option = from.Options.FirstOrDefault(o => o.Id == request.Option);
                if (option is null) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_option", "请选择一个选项"));
                target = story.ById.GetValueOrDefault(option.TargetEpisodeId);
                break;
            case SceneOutcome.Continue:
                target = from.NextEpisodeId is { } next ? story.ById.GetValueOrDefault(next) : null;
                break;
            default:
                return TypedResults.BadRequest(ApiErrorResponse.Of("story_ended", "这个片段之后没有剧情了"));
        }
        if (target is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "下一段剧情暂时无法播放"));

        int? share = null;
        if (option is not null)
        {
            var timedOut = request.TimedOut ? 1 : 0;
            await db.StoryOptions.Where(o => o.Id == option.Id).ExecuteUpdateAsync(u => u
                .SetProperty(o => o.ChosenCount, o => o.ChosenCount + 1)
                .SetProperty(o => o.TimeoutCount, o => o.TimeoutCount + timedOut), ct);
            var counts = await db.StoryOptions.AsNoTracking().Where(o => o.FromEpisodeId == from.Id).Select(o => new { o.Id, o.ChosenCount }).ToListAsync(ct);
            var total = counts.Sum(c => c.ChosenCount);
            if (total >= ShareAfter) share = (int)Math.Round(100.0 * counts.Single(c => c.Id == option.Id).ChosenCount / total);
        }

        int? ending = story.EndingNumbers.TryGetValue(target.Id, out var n) ? n : null;
        if (await UserAuth.OptionalUserIdAsync(http) is { } uid)
        {
            List<StoryStep> steps = option is null ? [] : [new(from.EpisodeNumber, option.Id)];
            // A paid ending the viewer can't watch yet isn't found yet: it counts once they unlock it and play it
            // (the player then saves it through /progress).
            var series = story.Series;
            var watchable = Pricing.EpisodePrice(billing.Value.Enabled, series.EpisodePrice, series.FreeEpisodes, target.EpisodeNumber, target.IsFree, target.CustomPrice) == 0
                || await wallets.CanWatchAsync(uid, id, target.EpisodeNumber, ct);
            await SaveAsync(db, uid, story, target.EpisodeNumber, steps, append: true, extraEndings: [], countScene: watchable, time, ct);
        }
        return TypedResults.Ok(new StoryNextDto(target.EpisodeNumber, ending, ending is null ? null : target.EndingTitle, share));
    }

    private static async Task<Results<Ok<StoryProgressDto>, NotFound<ApiErrorResponse>, BadRequest<ApiErrorResponse>>> SaveProgress(
        long id, StoryProgressRequest request, HttpContext http, ReelGuoDbContext db, ICountryResolver resolver, TimeProvider time, CancellationToken ct)
    {
        var story = await LoadAsync(id, http, db, resolver, time, ct);
        if (story is null) return Missing();
        if (story.ByNumber(request.Scene) is null) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_scene", "片段不存在"));

        var steps = (request.Path ?? []).Select(s => new StoryStep(s.Scene, s.Option));
        var saved = await SaveAsync(db, UserAuth.UserId(http), story, request.Scene, steps, append: false, request.Endings ?? [], countScene: true, time, ct);
        return TypedResults.Ok(ToDto(saved, story));
    }

    /// <summary>Writes the viewer's place; <paramref name="extraEndings"/>, and the scene when <paramref name="countScene"/> and
    /// it is an ending, add to the endings found.</summary>
    private static async Task<StoryProgress> SaveAsync(
        ReelGuoDbContext db, long userId, Loaded story, int scene, IEnumerable<StoryStep> steps, bool append, IEnumerable<int> extraEndings,
        bool countScene, TimeProvider time, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var saved = await db.StoryProgress.FirstOrDefaultAsync(p => p.UserId == userId && p.SeriesId == story.Series.Id, ct);
            var isNew = saved is null;
            saved ??= new StoryProgress { UserId = userId, SeriesId = story.Series.Id };

            saved.Path = ValidSteps(append ? saved.Path.Concat(steps) : steps, story);
            saved.Scene = scene;
            var endings = saved.Endings.Concat(extraEndings).Concat(countScene ? [scene] : [])
                .Where(n => story.ByNumber(n) is { Outcome: SceneOutcome.Ending }).Distinct().Order().ToList();
            if (!endings.SequenceEqual(saved.Endings)) saved.Endings = endings;
            saved.UpdatedAt = time.GetUtcNow();
            if (isNew) db.StoryProgress.Add(saved);
            try
            {
                await db.SaveChangesAsync(ct);
                return saved;
            }
            catch (DbUpdateException) when (isNew && attempt == 0)
            {
                // Two requests created the row at once: load the one that won and apply this step to it.
                db.ChangeTracker.Clear();
            }
        }
    }
}
