using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReelGuo.Api.Users;
using ReelGuo.Data;
using ReelGuo.Data.Catalog;
using ReelGuo.Data.Entities;
using static ReelGuo.Api.Tests.HttpTestExtensions;

namespace ReelGuo.Api.Tests;

/// <summary>The rules an interactive series' story is checked against (no database).</summary>
public sealed class StoryRulesTests
{
    private static StorySceneSpec Choice(long id, params (string Label, long To, bool Default)[] options) =>
        new(id, (int)id, true, SceneOutcome.Choice, "你要？", 0, null, null, null,
            options.Select(o => new StoryOptionSpec(o.Label, null, o.To, o.Default)).ToList());

    private static StorySceneSpec Continue(long id, long next) => new(id, (int)id, true, SceneOutcome.Continue, null, 0, null, next, null, []);

    private static StorySceneSpec Ending(long id, string? title = "结局") => new(id, (int)id, true, SceneOutcome.Ending, null, 0, null, null, title, []);

    /// <summary>The plan's example, 《深夜来客》: S1 → S2 / S3; S2 → E5 / S4; S3 continues to S4; S4 → E6 / E7.</summary>
    private static List<StorySceneSpec> Example() =>
    [
        Choice(1, ("开门", 2, true), ("隔门说话", 3, false)),
        Choice(2, ("留下帮她", 4, true), ("送她走", 5, false)),
        Continue(3, 4),
        Choice(4, ("说出实情", 6, true), ("替她隐瞒", 7, false)),
        Ending(5), Ending(6), Ending(7),
    ];

    [Fact]
    public void A_complete_story_has_no_problems()
    {
        Assert.Empty(StoryRules.Check(Example(), null));
        Assert.Equal(1, StoryRules.StartOf(Example(), null));
        Assert.Equal(1, StoryRules.StartOf(Example(), 999));
    }

    [Fact]
    public void Only_scenes_viewers_can_reach_must_be_finished()
    {
        var scenes = Example();
        // A spare scene nothing leads to: still uploading and not set up, which is only worth a warning.
        scenes.Add(new StorySceneSpec(8, 8, false, SceneOutcome.Unset, null, 0, null, null, null, []));
        var problems = StoryRules.Check(scenes, null);
        var only = Assert.Single(problems);
        Assert.Equal(StoryProblemLevel.Warning, only.Level);
        Assert.Equal(8, only.EpisodeId);

        // Once an option leads there, it has to be ready and set up.
        scenes[3] = Choice(4, ("说出实情", 6, true), ("替她隐瞒", 8, false));
        var errors = StoryRules.Check(scenes, null).Where(p => p.Level == StoryProblemLevel.Error).Select(p => p.Message).ToList();
        Assert.Contains("S8 的视频还没处理完成", errors);
        Assert.Contains("S8 还没设置播完后怎么走", errors);
    }

    [Fact]
    public void Choices_need_two_to_four_labelled_options_and_a_default_with_a_countdown()
    {
        var scenes = Example();
        scenes[0] = Choice(1, ("开门", 2, false)) with { Countdown = 10 };
        var errors = StoryRules.Check(scenes, null).Select(p => p.Message).ToList();
        Assert.Contains("S1 需要 2-4 个选项", errors);
        Assert.Contains("S1 设置了倒计时，需要指定一个默认选项", errors);

        scenes[0] = Choice(1, ("", 2, true), ("隔门说话", 3, true));
        errors = StoryRules.Check(scenes, null).Select(p => p.Message).ToList();
        Assert.Contains("S1 的第 1 个选项还没写按钮文字", errors);
        Assert.Contains("S1 只能有一个默认选项", errors);
    }

    [Fact]
    public void A_choice_whose_options_all_have_conditions_gets_a_warning()
    {
        var scenes = Example();
        scenes[3] = scenes[3] with { Options = [.. scenes[3].Options.Select(o => o with { Conditional = true })] };
        var only = Assert.Single(StoryRules.Check(scenes, null));
        Assert.Equal((StoryProblemLevel.Warning, 4L), (only.Level, only.EpisodeId!.Value));
    }

    [Fact]
    public void A_scene_s_own_price_replaces_the_series_rule()
    {
        Assert.Equal(60, Pricing.EpisodePrice(true, seriesPrice: 0, freeEpisodes: 3, episodeNumber: 7, episodeIsFree: false, customPrice: 60));
        Assert.Equal(60, Pricing.EpisodePrice(true, 30, 10, 2, false, 60));
        Assert.Equal(0, Pricing.EpisodePrice(true, 30, 0, 7, false, 0));
        Assert.Equal(0, Pricing.EpisodePrice(true, 30, 0, 7, episodeIsFree: true, customPrice: 60));
        Assert.Equal(0, Pricing.EpisodePrice(false, 30, 0, 7, false, 60));
        Assert.Equal(30, Pricing.EpisodePrice(true, 30, 3, 7, false));
    }

    [Fact]
    public void Endings_need_names_and_must_be_reachable_without_endless_loops()
    {
        var scenes = Example();
        scenes[4] = Ending(5, " ");
        Assert.Contains("S5 是结局，需要填写结局名称", StoryRules.Check(scenes, null).Select(p => p.Message));

        // S2 and S3 send each other back and forth: no ending can be reached from the start.
        List<StorySceneSpec> loop = [Choice(1, ("A", 2, false), ("B", 3, false)), Continue(2, 3), Continue(3, 2), Ending(4)];
        var problems = StoryRules.Check(loop, null);
        Assert.Contains(problems, p => p is { Level: StoryProblemLevel.Error, Message: "从起点走不到任何结局，至少需要一个结局" });

        // With an ending elsewhere in the story, the loop is a warning on the scenes it traps.
        loop[0] = Choice(1, ("A", 2, false), ("B", 4, false));
        problems = StoryRules.Check(loop, null);
        Assert.DoesNotContain(problems, p => p.Level == StoryProblemLevel.Error);
        Assert.Equal([2, 3], problems.Where(p => p.Message.Contains("绕圈")).Select(p => p.EpisodeId!.Value).Order());
    }
}

/// <summary>互动剧 end to end: the admin builds and publishes a story, viewers choose their way through it.</summary>
public sealed class StoryTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    private sealed record Viewer(long Id, string Token);

    private sealed record Seeded(long Id, Dictionary<int, long> Scenes);

    private static readonly string[] Titles = ["开场：深夜敲门", "屋内叙旧", "门缝里的纸条", "警察上门", "结局·各自安好", "结局·真相", "结局·共犯"];

    /// <summary>An interactive series with seven ready scenes (no story yet unless <paramref name="withStory"/>).</summary>
    private static async Task<Seeded> SeedAsync(ApiFactory api, SeriesStatus status, bool withStory)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ReelGuoDbContext>();
        var now = DateTimeOffset.UtcNow;
        var series = new Series
        {
            Title = "深夜来客", CategoryId = 1, Status = status, Kind = SeriesKind.Interactive, PublishedAt = status == SeriesStatus.Published ? now.AddDays(-1) : null,
            EpisodeCount = 7, TotalEpisodes = 7, Territories = ["*"], RightsHolder = "T", AiGenerated = true, HasRealPerson = false,
            CreatedAt = now, UpdatedAt = now,
            Episodes = Titles.Select((t, i) => new Episode
            {
                EpisodeNumber = i + 1, Title = t, DurationSeconds = 90, Status = EpisodeStatus.Ready, HlsPrefix = $"s/0/e/{i + 1}/v1", CreatedAt = now, UpdatedAt = now,
            }).ToList(),
        };
        ctx.Series.Add(series);
        await ctx.SaveChangesAsync(Ct);
        var ids = series.Episodes.ToDictionary(e => e.EpisodeNumber, e => e.Id);
        if (withStory)
        {
            void Choose(int scene, string prompt, params (string Label, int To, bool Default)[] options)
            {
                var e = series.Episodes.Single(x => x.EpisodeNumber == scene);
                e.Outcome = SceneOutcome.Choice;
                e.ChoicePrompt = prompt;
                e.ChoiceCountdown = 10;
                e.Options = options.Select((o, i) => new StoryOption
                {
                    SeriesId = series.Id, SortOrder = i, Label = o.Label, TargetEpisodeId = ids[o.To], IsDefault = o.Default,
                }).ToList();
            }
            Choose(1, "门外是消失三年的前任，你要？", ("开门让她进来", 2, true), ("隔着门说话", 3, false));
            Choose(2, "她说自己惹上了麻烦，你要？", ("留下帮她", 4, true), ("送她离开", 5, false));
            Choose(4, "警察问你有没有见过她，你要？", ("说出实情", 6, true), ("替她隐瞒", 7, false));
            var s3 = series.Episodes.Single(x => x.EpisodeNumber == 3);
            s3.Outcome = SceneOutcome.Continue;
            s3.NextEpisodeId = ids[4];
            foreach (var (n, title) in new[] { (5, "各自安好"), (6, "真相"), (7, "共犯") })
            {
                var e = series.Episodes.Single(x => x.EpisodeNumber == n);
                e.Outcome = SceneOutcome.Ending;
                e.EndingTitle = title;
            }
            await ctx.SaveChangesAsync(Ct);
        }
        return new Seeded(series.Id, ids);
    }

    /// <summary>The story as the admin editor would save it (the same graph as <see cref="SeedAsync"/>).</summary>
    private static object StoryBody(Seeded s, int s4Options = 2) => new
    {
        start_episode_id = s.Scenes[1],
        scenes = new object[]
        {
            new { episode_id = s.Scenes[1], outcome = "choice", prompt = "门外是消失三年的前任，你要？", lead_seconds = 0, countdown = 10,
                options = new[] { Option("开门让她进来", s.Scenes[2], true, "听听她想说什么"), Option("隔着门说话", s.Scenes[3], false) } },
            new { episode_id = s.Scenes[2], outcome = "choice", prompt = "她说自己惹上了麻烦，你要？", lead_seconds = 3, countdown = (int?)null,
                options = new[] { Option("留下帮她", s.Scenes[4], false), Option("送她离开", s.Scenes[5], false) } },
            new { episode_id = s.Scenes[3], outcome = "continue", next_episode_id = s.Scenes[4] },
            new { episode_id = s.Scenes[4], outcome = "choice", prompt = "警察问你有没有见过她，你要？", lead_seconds = 0, countdown = (int?)null,
                options = new[] { Option("说出实情", s.Scenes[6], false), Option("替她隐瞒", s.Scenes[7], false) }.Take(s4Options).ToArray() },
            new { episode_id = s.Scenes[5], outcome = "ending", ending_title = "各自安好" },
            new { episode_id = s.Scenes[6], outcome = "ending", ending_title = "真相" },
            new { episode_id = s.Scenes[7], outcome = "ending", ending_title = "共犯" },
        },
    };

    private static object Option(string label, long target, bool isDefault, string? description = null) =>
        new { id = (long?)null, label, description, target_episode_id = target, is_default = isDefault };

    private static async Task<Viewer> RegisterAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/register")
        {
            Content = JsonContent.Create(new { username = $"s{Guid.NewGuid():N}"[..14], password = "viewer-pass-1", accept_terms = true }),
        };
        request.Headers.Add(UserAuth.AppHeader, "1");
        var body = await client.SendAsync(request, Ct).OkJsonAsync();
        return new Viewer(body.GetProperty("user").GetProperty("id").GetInt64(), body.GetProperty("access_token").GetString()!);
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string url, Viewer? viewer = null, object? body = null)
    {
        var request = new HttpRequestMessage(method, url).From("US");
        if (viewer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewer.Token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return client.SendAsync(request, Ct);
    }

    private static Task<JsonElement> NextAsync(HttpClient client, long series, int from, long? option, Viewer? viewer = null, bool timedOut = false) =>
        Send(client, HttpMethod.Post, $"/api/v1/series/{series}/story/next", viewer, new { from, option, timed_out = timedOut }).OkJsonAsync();

    private static JsonElement Scene(JsonElement story, int ep) =>
        story.GetProperty("scenes").EnumerateArray().Single(s => s.GetProperty("ep_num").GetInt32() == ep);

    private static long OptionId(JsonElement story, int ep, int index) =>
        Scene(story, ep).GetProperty("options")[index].GetProperty("id").GetInt64();

    [Fact]
    public async Task The_admin_sets_up_the_story_and_publishing_waits_until_it_is_complete()
    {
        await using var api = db.CreateApi();
        var admin = await AdminClient.SignInAsync(api);
        var s = await SeedAsync(api, SeriesStatus.Draft, withStory: false);

        var empty = await admin.GetAsync($"/api/admin/series/{s.Id}/story", Ct).OkJsonAsync();
        Assert.Equal("interactive", empty.GetProperty("kind").GetString());
        Assert.Equal(s.Scenes[1], empty.GetProperty("effective_start_episode_id").GetInt64());
        Assert.Contains(empty.GetProperty("problems").EnumerateArray(), p => p.GetProperty("message").GetString() == "S1 还没设置播完后怎么走");

        var blocked = await admin.AdminSendAsync(HttpMethod.Post, $"/api/admin/series/{s.Id}/publish");
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        Assert.Contains("剧情：S1 还没设置播完后怎么走", (await blocked.JsonAsync()).GetProperty("error").GetProperty("details").EnumerateArray().Select(d => d.GetString()));

        // A draft may be saved unfinished (S4 with one option), with the problems listed.
        var draft = await admin.AdminSendAsync(HttpMethod.Put, $"/api/admin/series/{s.Id}/story", StoryBody(s, s4Options: 1)).OkJsonAsync();
        Assert.Contains(draft.GetProperty("problems").EnumerateArray(), p => p.GetProperty("message").GetString() == "S4 需要 2-4 个选项");

        var saved = await admin.AdminSendAsync(HttpMethod.Put, $"/api/admin/series/{s.Id}/story", StoryBody(s)).OkJsonAsync();
        Assert.DoesNotContain(saved.GetProperty("problems").EnumerateArray(), p => p.GetProperty("level").GetString() == "error");
        var s1 = saved.GetProperty("scenes")[0];
        Assert.Equal("choice", s1.GetProperty("outcome").GetString());
        Assert.Equal("听听她想说什么", s1.GetProperty("options")[0].GetProperty("description").GetString());
        var keptId = s1.GetProperty("options")[0].GetProperty("id").GetInt64();

        await admin.AdminSendAsync(HttpMethod.Post, $"/api/admin/series/{s.Id}/publish").OkJsonAsync();

        // Live now: a save that would break the story is refused, a good one keeps option ids (and their statistics).
        var broken = await admin.AdminSendAsync(HttpMethod.Put, $"/api/admin/series/{s.Id}/story", StoryBody(s, s4Options: 1));
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
        Assert.Equal("story_invalid", await broken.ErrorCodeAsync());

        var body = JsonSerializer.SerializeToNode(StoryBody(s))!;
        body["scenes"]![0]!["options"]![0]!["id"] = keptId;
        var resaved = await admin.AdminSendAsync(HttpMethod.Put, $"/api/admin/series/{s.Id}/story", body).OkJsonAsync();
        Assert.Equal(keptId, resaved.GetProperty("scenes")[0].GetProperty("options")[0].GetProperty("id").GetInt64());

        // Bad input is refused whatever the status.
        var tooLong = JsonSerializer.SerializeToNode(StoryBody(s))!;
        tooLong["scenes"]![0]!["options"]![0]!["label"] = "一二三四五六七八九十一二三";
        Assert.Equal("invalid_label", await (await admin.AdminSendAsync(HttpMethod.Put, $"/api/admin/series/{s.Id}/story", tooLong)).ErrorCodeAsync());

        // S3 is where 「隔着门说话」 leads: it can't be deleted while the story uses it.
        var delete = await admin.AdminSendAsync(HttpMethod.Delete, $"/api/admin/episodes/{s.Scenes[3]}");
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal("scene_in_use", await delete.ErrorCodeAsync());

        // An ordinary series has no story editor.
        var plain = await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/series",
            new { category_slug = "ai-duanju", title = "普通剧", score = 0, is_finished = false, is_original = true, total_episodes = 0 }).OkJsonAsync();
        Assert.Equal("standard", plain.GetProperty("kind").GetString());
        Assert.Equal("not_interactive", await (await admin.AdminSendAsync(HttpMethod.Put, $"/api/admin/series/{plain.GetProperty("id").GetInt64()}/story",
            new { scenes = Array.Empty<object>() })).ErrorCodeAsync());
    }

    [Fact]
    public async Task Viewers_choose_their_way_through_and_their_place_is_kept()
    {
        await using var api = db.CreateApi();
        var client = api.CreateClient();
        var s = await SeedAsync(api, SeriesStatus.Published, withStory: true);

        // A guest sees the story without the endings' names.
        var story = await Send(client, HttpMethod.Get, $"/api/v1/series/{s.Id}/story").OkJsonAsync();
        Assert.Equal(1, story.GetProperty("start").GetInt32());
        Assert.Equal(3, story.GetProperty("ending_count").GetInt32());
        Assert.Equal(JsonValueKind.Null, story.GetProperty("progress").ValueKind);
        var e1 = Scene(story, 5);
        Assert.Equal(("ending", 1, ""), (e1.GetProperty("outcome").GetString(), e1.GetProperty("ending").GetInt32(), e1.GetProperty("title").GetString()));
        Assert.Equal(JsonValueKind.Null, e1.GetProperty("ending_title").ValueKind);
        Assert.Equal([2, 3], Scene(story, 1).GetProperty("options").EnumerateArray().Select(o => o.GetProperty("target").GetInt32()));
        Assert.Equal(4, Scene(story, 3).GetProperty("next").GetInt32());

        var detail = await Send(client, HttpMethod.Get, $"/api/v1/series/{s.Id}").OkJsonAsync();
        Assert.Equal("interactive", detail.GetProperty("kind").GetString());
        Assert.Equal("", detail.GetProperty("episodes")[4].GetProperty("title").GetString());

        // Choose, continue, choose: the paid-looking ending arrives with its name.
        Assert.Equal(3, (await NextAsync(client, s.Id, 1, OptionId(story, 1, 1))).GetProperty("scene").GetInt32());
        Assert.Equal(4, (await NextAsync(client, s.Id, 3, null)).GetProperty("scene").GetInt32());
        var end = await NextAsync(client, s.Id, 4, OptionId(story, 4, 1), timedOut: true);
        Assert.Equal((7, 3, "共犯"), (end.GetProperty("scene").GetInt32(), end.GetProperty("ending").GetInt32(), end.GetProperty("ending_title").GetString()));
        Assert.Equal(JsonValueKind.Null, end.GetProperty("share").ValueKind);

        Assert.Equal("story_ended", await (await Send(client, HttpMethod.Post, $"/api/v1/series/{s.Id}/story/next", body: new { from = 7 })).ErrorCodeAsync());
        Assert.Equal("invalid_option", await (await Send(client, HttpMethod.Post, $"/api/v1/series/{s.Id}/story/next",
            body: new { from = 1, option = OptionId(story, 4, 0) })).ErrorCodeAsync());

        // A signed-in viewer's choices and endings are saved, and the endings they found are named.
        var viewer = await RegisterAsync(client);
        await NextAsync(client, s.Id, 1, OptionId(story, 1, 0), viewer);
        await NextAsync(client, s.Id, 2, OptionId(story, 2, 1), viewer);
        var mine = await Send(client, HttpMethod.Get, $"/api/v1/series/{s.Id}/story", viewer).OkJsonAsync();
        var progress = mine.GetProperty("progress");
        Assert.Equal(5, progress.GetProperty("scene").GetInt32());
        Assert.Equal([1, 2], progress.GetProperty("path").EnumerateArray().Select(p => p.GetProperty("scene").GetInt32()));
        Assert.Equal([5], progress.GetProperty("endings").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal("各自安好", Scene(mine, 5).GetProperty("ending_title").GetString());
        Assert.Equal("", Scene(mine, 6).GetProperty("title").GetString());

        // Going back to the first choice keeps the endings found, and drops steps that don't exist.
        var back = await Send(client, HttpMethod.Put, $"/api/v1/series/{s.Id}/story/progress", viewer,
            new { scene = 1, path = new[] { new { scene = 3, option = 42L } }, endings = new[] { 6, 2 } }).OkJsonAsync();
        Assert.Equal(1, back.GetProperty("scene").GetInt32());
        Assert.Empty(back.GetProperty("path").EnumerateArray());
        Assert.Equal([5, 6], back.GetProperty("endings").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, HttpMethod.Put, $"/api/v1/series/{s.Id}/story/progress", body: new { scene = 1 })).StatusCode);

        // 「64% 的观众也这么选」 once 100 viewers chose at that point.
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ReelGuoDbContext>();
            var first = OptionId(story, 1, 0);
            await ctx.StoryOptions.Where(o => o.FromEpisodeId == s.Scenes[1])
                .ExecuteUpdateAsync(u => u.SetProperty(o => o.ChosenCount, o => o.Id == first ? 63 : 36), Ct);
        }
        Assert.Equal(64, (await NextAsync(client, s.Id, 1, OptionId(story, 1, 0))).GetProperty("share").GetInt32());

        await using (var scope = api.Services.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ReelGuoDbContext>();
            var counted = await ctx.StoryOptions.SingleAsync(o => o.Id == OptionId(story, 4, 1), Ct);
            Assert.Equal((1, 1), (counted.ChosenCount, counted.TimeoutCount));
        }
    }

    [Fact]
    public async Task Scores_conditions_scene_prices_and_the_map_layout_are_saved_and_shown()
    {
        await using var api = db.CreateApi();
        var client = api.CreateClient();
        var admin = await AdminClient.SignInAsync(api);
        var s = await SeedAsync(api, SeriesStatus.Published, withStory: true);
        var url = $"/api/admin/series/{s.Id}/story";

        // 好感度: 开门 +2; 说出实情 shows only at 好感度 ≥ 2; the 共犯 ending costs 60 coins in an otherwise free series.
        var body = JsonSerializer.SerializeToNode(StoryBody(s))!;
        body["variables"] = JsonSerializer.SerializeToNode(new[] { new { key = "v1", name = "好感度", initial = 0 } });
        body["layout"] = JsonSerializer.SerializeToNode(new[] { new { episode_id = s.Scenes[1], x = 40, y = 300 } });
        body["scenes"]![0]!["options"]![0]!["effects"] = JsonSerializer.SerializeToNode(new[] { new { variable = "v1", add = 2 } });
        body["scenes"]![3]!["options"]![0]!["conditions"] = JsonSerializer.SerializeToNode(new[] { new { variable = "v1", op = "gte", value = 2 } });
        body["scenes"]![6]!["custom_price"] = 60;
        var saved = await admin.AdminSendAsync(HttpMethod.Put, url, body).OkJsonAsync();
        Assert.Equal("好感度", saved.GetProperty("variables")[0].GetProperty("name").GetString());
        Assert.Equal(300, saved.GetProperty("layout")[0].GetProperty("y").GetInt32());
        var e3 = saved.GetProperty("scenes")[6];
        Assert.Equal((60, 60), (e3.GetProperty("custom_price").GetInt32(), e3.GetProperty("price").GetInt32()));
        Assert.Equal(2, saved.GetProperty("scenes")[0].GetProperty("options")[0].GetProperty("effects")[0].GetProperty("add").GetInt32());

        // Viewers get the scores, effects and conditions; the ending's own price applies to playing and unlocking it.
        var story = await Send(client, HttpMethod.Get, $"/api/v1/series/{s.Id}/story").OkJsonAsync();
        Assert.Equal("v1", story.GetProperty("variables")[0].GetProperty("key").GetString());
        var condition = Scene(story, 4).GetProperty("options")[0].GetProperty("conditions")[0];
        Assert.Equal(("gte", 2), (condition.GetProperty("op").GetString(), condition.GetProperty("value").GetInt32()));
        Assert.Equal((60, false), (Scene(story, 7).GetProperty("price").GetInt32(), Scene(story, 7).GetProperty("is_free").GetBoolean()));
        Assert.Equal("episode_locked", await (await Send(client, HttpMethod.Get, $"/api/v1/series/{s.Id}/episodes/7/play")).ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Get, $"/api/v1/series/{s.Id}/episodes/6/play")).StatusCode);
        var viewer = await RegisterAsync(client);
        await admin.AdminSendAsync(HttpMethod.Post, $"/api/admin/members/{viewer.Id}/wallet/adjust",
            new { coins = 100, bonus_coins = 0, vip_days = 0, note = "测试发放" }).OkJsonAsync();
        var unlocked = await Send(client, HttpMethod.Post, $"/api/v1/series/{s.Id}/episodes/7/unlock", viewer).OkJsonAsync();
        Assert.Equal(60, unlocked.GetProperty("spent").GetInt32());

        // Scores in use can't be removed, and unknown scores are refused.
        var noVariables = JsonSerializer.SerializeToNode(StoryBody(s))!;
        noVariables["variables"] = new System.Text.Json.Nodes.JsonArray();
        noVariables["scenes"] = new System.Text.Json.Nodes.JsonArray();
        Assert.Equal("variable_in_use", await (await admin.AdminSendAsync(HttpMethod.Put, url, noVariables)).ErrorCodeAsync());
        var unknown = JsonSerializer.SerializeToNode(StoryBody(s))!;
        unknown["scenes"]![0]!["options"]![0]!["effects"] = JsonSerializer.SerializeToNode(new[] { new { variable = "nope", add = 1 } });
        Assert.Equal("invalid_option_logic", await (await admin.AdminSendAsync(HttpMethod.Put, url, unknown)).ErrorCodeAsync());

        // An empty layout returns to the automatic one.
        var auto = JsonSerializer.SerializeToNode(new { scenes = Array.Empty<object>(), layout = Array.Empty<object>() })!;
        Assert.Empty((await admin.AdminSendAsync(HttpMethod.Put, url, auto).OkJsonAsync()).GetProperty("layout").EnumerateArray());
    }

    [Fact]
    public async Task A_paid_ending_is_found_only_once_the_viewer_can_watch_it()
    {
        await using var api = db.CreateApi();
        var client = api.CreateClient();
        var admin = await AdminClient.SignInAsync(api);
        var s = await SeedAsync(api, SeriesStatus.Published, withStory: true);
        await using (var scope = api.Services.CreateAsyncScope())
        {
            // S1-S6 free, S7 (结局·共犯) 30 coins.
            var ctx = scope.ServiceProvider.GetRequiredService<ReelGuoDbContext>();
            await ctx.Series.Where(x => x.Id == s.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.EpisodePrice, 30).SetProperty(x => x.FreeEpisodes, 6), Ct);
        }
        var viewer = await RegisterAsync(client);
        var story = await Send(client, HttpMethod.Get, $"/api/v1/series/{s.Id}/story", viewer).OkJsonAsync();
        Assert.Equal((30, false), (Scene(story, 7).GetProperty("price").GetInt32(), Scene(story, 7).GetProperty("is_free").GetBoolean()));

        await NextAsync(client, s.Id, 1, OptionId(story, 1, 1), viewer);
        await NextAsync(client, s.Id, 3, null, viewer);
        Assert.Equal(7, (await NextAsync(client, s.Id, 4, OptionId(story, 4, 1), viewer)).GetProperty("scene").GetInt32());
        var locked = (await Send(client, HttpMethod.Get, $"/api/v1/series/{s.Id}/story", viewer).OkJsonAsync()).GetProperty("progress");
        Assert.Equal(7, locked.GetProperty("scene").GetInt32());
        Assert.Empty(locked.GetProperty("endings").EnumerateArray());

        // Unlocked and played: the player saves the ending.
        await admin.AdminSendAsync(HttpMethod.Post, $"/api/admin/members/{viewer.Id}/wallet/adjust",
            new { coins = 100, bonus_coins = 0, vip_days = 0, note = "测试发放" }).OkJsonAsync();
        await Send(client, HttpMethod.Post, $"/api/v1/series/{s.Id}/episodes/7/unlock", viewer).OkJsonAsync();
        var found = await Send(client, HttpMethod.Put, $"/api/v1/series/{s.Id}/story/progress", viewer,
            new { scene = 7, path = locked.GetProperty("path"), endings = new[] { 7 } }).OkJsonAsync();
        Assert.Equal([7], found.GetProperty("endings").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(2, found.GetProperty("path").GetArrayLength());
    }

    [Fact]
    public async Task Ordinary_series_have_no_story_and_deleting_a_story_series_removes_everything()
    {
        await using var api = db.CreateApi();
        var client = api.CreateClient();
        var admin = await AdminClient.SignInAsync(api);
        var s = await SeedAsync(api, SeriesStatus.Published, withStory: true);
        var viewer = await RegisterAsync(client);
        var story = await Send(client, HttpMethod.Get, $"/api/v1/series/{s.Id}/story").OkJsonAsync();
        await NextAsync(client, s.Id, 1, OptionId(story, 1, 0), viewer);

        long plainId;
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ReelGuoDbContext>();
            plainId = await ctx.Series.Where(x => x.Id != s.Id && x.Kind == SeriesKind.Standard).Select(x => (long?)x.Id).FirstOrDefaultAsync(Ct) ?? 0;
        }
        if (plainId != 0) Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Get, $"/api/v1/series/{plainId}/story")).StatusCode);

        await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/series/delete", new { ids = new[] { s.Id } }).OkJsonAsync();
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ReelGuoDbContext>();
            Assert.False(await ctx.StoryOptions.AnyAsync(o => o.SeriesId == s.Id, Ct));
            Assert.False(await ctx.StoryProgress.AnyAsync(p => p.SeriesId == s.Id, Ct));
            Assert.False(await ctx.Episodes.AnyAsync(e => e.SeriesId == s.Id, Ct));
        }
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Get, $"/api/v1/series/{s.Id}/story")).StatusCode);
    }
}
