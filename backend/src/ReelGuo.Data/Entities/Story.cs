using System.Text.Json;

namespace ReelGuo.Data.Entities;

/// <summary>One option (选项) shown when a scene of an interactive series ends: a button that leads to another scene.</summary>
public sealed class StoryOption
{
    public long Id { get; set; }
    public long SeriesId { get; set; }
    /// <summary>The scene whose end shows this option.</summary>
    public long FromEpisodeId { get; set; }
    public Episode From { get; set; } = null!;
    public int SortOrder { get; set; }
    /// <summary>Button text, up to <see cref="Catalog.StoryRules.MaxLabel"/> characters.</summary>
    public string Label { get; set; } = "";
    /// <summary>Optional line under the button text.</summary>
    public string? Description { get; set; }
    public long TargetEpisodeId { get; set; }
    public Episode Target { get; set; } = null!;
    /// <summary>Plays when the countdown runs out.</summary>
    public bool IsDefault { get; set; }
    /// <summary>How taking this option changes the story's hidden scores (jsonb, <see cref="StoryEffect"/>[]).</summary>
    public string EffectsJson { get; set; } = "[]";
    /// <summary>The option shows only when all of these hold (jsonb, <see cref="StoryCondition"/>[]).</summary>
    public string ConditionsJson { get; set; } = "[]";

    /// <summary>How often viewers took this option, and how many of those were the countdown choosing for them.</summary>
    public long ChosenCount { get; set; }
    public long TimeoutCount { get; set; }
}

/// <summary>A signed-in viewer's place in an interactive series: the scene they are on, the choices that led there, the endings found.</summary>
public sealed class StoryProgress
{
    public long UserId { get; set; }
    public long SeriesId { get; set; }
    /// <summary>Episode number of the current scene.</summary>
    public int Scene { get; set; }
    /// <summary>The choices made, oldest first (jsonb, <see cref="StoryStep"/>[]).</summary>
    public string PathJson { get; set; } = "[]";
    /// <summary>Episode numbers of the ending scenes reached.</summary>
    public List<int> Endings { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public List<StoryStep> Path
    {
        get
        {
            try { return JsonSerializer.Deserialize<List<StoryStep>>(PathJson, Json) ?? []; }
            catch (JsonException) { return []; }
        }
        set => PathJson = JsonSerializer.Serialize(value, Json);
    }
}

/// <summary>One choice: at scene <paramref name="Scene"/> (episode number) the viewer took option <paramref name="Option"/>.</summary>
public sealed record StoryStep(int Scene, long Option);

/// <summary>A hidden score of an interactive series, e.g. 好感度 starting at 0. <paramref name="Key"/> is stable across renames.</summary>
public sealed record StoryVariable(string Key, string Name, int Initial);

/// <summary>Taking an option adds <paramref name="Add"/> (negative to subtract) to a score.</summary>
public sealed record StoryEffect(string Variable, int Add);

/// <summary>An option shows only when the score compares to <paramref name="Value"/>: <paramref name="Op"/> is gte (≥), lte (≤) or eq (=).</summary>
public sealed record StoryCondition(string Variable, string Op, int Value)
{
    public static readonly string[] Ops = ["gte", "lte", "eq"];

    public bool Holds(int score) => Op switch { "gte" => score >= Value, "lte" => score <= Value, "eq" => score == Value, _ => true };
}

/// <summary>A scene's place on the admin's story map.</summary>
public sealed record MapPoint(int X, int Y);

/// <summary>Reads and writes the story's jsonb columns; unreadable values count as empty.</summary>
public static class StoryJson
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static List<T> List<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<T>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static Dictionary<long, MapPoint> Layout(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<long, MapPoint>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Json);
}
