namespace ReelGuo.Data.Entities;

public enum CategoryStatus
{
    Active,
    /// <summary>Listed in the app with a "coming soon" state; no content, no actions.</summary>
    ComingSoon,
    Hidden,
}

/// <summary>A browse channel such as 'AI成人短剧'. These are rows, not code, so admins can add, rename or hide them without an app release.</summary>
public sealed class Category
{
    public int Id { get; set; }
    public required string Slug { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public CategoryStatus Status { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<Series> Series { get; set; } = [];
}

/// <summary>The status badge on the series' cards, after 原创 (set in admin).</summary>
public enum CardBadge
{
    /// <summary>热播 while in the top 10 of the 热播榜, else 新片 for 7 days after publishing (series from before 2026-11-18).</summary>
    Auto,
    /// <summary>新片, always (the default for new series).</summary>
    New,
    /// <summary>热播, always.</summary>
    Hot,
    /// <summary>No status badge.</summary>
    None,
}

public enum SeriesStatus
{
    Draft,
    Published,
    Hidden,
    Removed,
}

public enum SeriesKind
{
    /// <summary>Numbered episodes played in order.</summary>
    Standard,
    /// <summary>互动剧: the episodes are scenes; when one ends the viewer picks what happens next (see <see cref="StoryOption"/>).</summary>
    Interactive,
}

/// <summary>What happens when a scene of an interactive series ends.</summary>
public enum SceneOutcome
{
    /// <summary>Not set up yet (publishing is blocked).</summary>
    Unset,
    /// <summary>The question and the options (<see cref="Episode.Options"/>) appear.</summary>
    Choice,
    /// <summary>Plays <see cref="Episode.NextEpisodeId"/> straight away.</summary>
    Continue,
    /// <summary>The story ends here, with <see cref="Episode.EndingTitle"/>.</summary>
    Ending,
}

/// <summary>A show made of numbered episodes (or, for <see cref="SeriesKind.Interactive"/>, of scenes). HGhub's client calls this a "video".</summary>
public sealed class Series
{
    public long Id { get; set; }
    public int CategoryId { get; set; }
    public Category Category { get; set; } = null!;
    public long? AuthorId { get; set; }
    public User? Author { get; set; }

    public required string Title { get; set; }
    public string Description { get; set; } = "";
    /// <summary>Media-bucket key of the cover image (public, under p/).</summary>
    public string? CoverKey { get; set; }
    public List<string> Tags { get; set; } = [];
    /// <summary>Editorial rating, 0-10 (shown as "8.6 分").</summary>
    public double Score { get; set; }
    /// <summary>Editor pick weight for 推荐榜 (0 = not picked; higher ranks first).</summary>
    public int RecommendWeight { get; set; }
    /// <summary>Coins per paid episode; 0 means the whole series is free (see <see cref="Catalog.Pricing"/>).</summary>
    public int EpisodePrice { get; set; }
    /// <summary>Leading episodes that are always free, so viewers can sample a paid series.</summary>
    public int FreeEpisodes { get; set; } = Catalog.Pricing.DefaultFreeEpisodes;
    public SeriesStatus Status { get; set; }
    public bool IsFinished { get; set; }
    /// <summary>原创: the 原创 badge on cards and a place in the 原创 listing. New series start as 原创.</summary>
    public bool IsOriginal { get; set; } = true;
    /// <summary>The status badge on cards; new series start with 新片.</summary>
    public CardBadge CardBadge { get; set; } = CardBadge.New;
    public SeriesKind Kind { get; set; }
    /// <summary>Interactive: the scene the story starts with; null means the lowest-numbered scene. Not a foreign key, so
    /// deleting that scene only leaves a publish problem.</summary>
    public long? StoryStartEpisodeId { get; set; }
    /// <summary>Interactive: hidden scores such as 好感度 (jsonb, <see cref="StoryVariable"/>[]), changed by options' effects.</summary>
    public string StoryVariablesJson { get; set; } = "[]";
    /// <summary>Interactive: where the admin placed each scene on the story map (jsonb, episode id → <see cref="MapPoint"/>);
    /// null or missing scenes are laid out automatically.</summary>
    public string? StoryLayoutJson { get; set; }
    public int TotalEpisodes { get; set; }
    public int EpisodeCount { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset? LastEpisodeAt { get; set; }

    // Denormalised counters, written from Redis in batches (Phase 1+).
    public long LikeCount { get; set; }
    public long CollectCount { get; set; }
    public long CommentCount { get; set; }
    public long PlayCount { get; set; }
    public double HotScore { get; set; }

    // Rights and consent. Publishing is blocked until these are filled in (Phase 1).
    /// <summary>The content source's own id, so re-running an import updates rather than duplicates.</summary>
    public string? ExternalId { get; set; }
    public string? RightsHolder { get; set; }
    public string? ContractRef { get; set; }
    public DateOnly? LicenceStart { get; set; }
    public DateOnly? LicenceEnd { get; set; }
    /// <summary>
    /// ISO 3166-1 alpha-2 codes where the licence allows distribution, or "*" for worldwide.
    /// Empty means not yet recorded (publishing is blocked).
    /// </summary>
    public List<string> Territories { get; set; } = [];
    /// <summary>Null until declared; publishing requires an explicit answer.</summary>
    public bool? AiGenerated { get; set; }
    /// <summary>Whether any real, identifiable person appears. Null until declared.</summary>
    public bool? HasRealPerson { get; set; }
    /// <summary>Reference to signed consent and ID records. Required when <see cref="HasRealPerson"/> is true.</summary>
    public string? ConsentRef { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<Episode> Episodes { get; set; } = [];
}

public enum EpisodeStatus
{
    Uploading,
    Processing,
    Ready,
    Failed,
}

public sealed class Episode
{
    public long Id { get; set; }
    public long SeriesId { get; set; }
    public Series Series { get; set; } = null!;

    /// <summary>1-based episode number, unique within a series.</summary>
    public int EpisodeNumber { get; set; }
    public string Title { get; set; } = "";
    public double DurationSeconds { get; set; }
    public EpisodeStatus Status { get; set; }

    /// <summary>
    /// Object key of the latest upload: in the private raw bucket ("uploads/…", "incoming/…"), or in the COS video
    /// bucket ("videos/…", see <see cref="ReelGuo.Data.Media.CosKeys"/>).
    /// </summary>
    public string? SourceKey { get; set; }
    /// <summary>Path prefix of the packaged HLS in the media bucket, e.g. s/123/e/3/v2. Null when the episode plays from COS.</summary>
    public string? HlsPrefix { get; set; }
    /// <summary>The COS object viewers play (an MP4 played as is). Null when the episode plays packaged HLS.</summary>
    public string? VideoKey { get; set; }
    /// <summary>Size of the COS object, as COS reported it when the upload was confirmed.</summary>
    public long? VideoBytes { get; set; }
    /// <summary>Bumped on every re-encode so CDN caches never serve stale segments.</summary>
    public int MediaVersion { get; set; }
    /// <summary>Rendition ladder produced by the worker (jsonb).</summary>
    public string? RenditionsJson { get; set; }
    /// <summary>Display size after rotation; lets the player choose cover vs. contain before the video loads.</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Media-bucket key of the poster frame (public, under p/).</summary>
    public string? PosterKey { get; set; }
    /// <summary>0-100 while processing.</summary>
    public int ProcessingProgress { get; set; }
    /// <summary>Why processing failed, shown in admin.</summary>
    public string? ProcessingError { get; set; }

    /// <summary>Free even when the series charges for this episode number (e.g. a promotional episode).</summary>
    public bool IsFree { get; set; }
    /// <summary>This episode's own price in coins, instead of the series' rule (e.g. a special ending of a 互动剧); null = the series' rule.</summary>
    public int? CustomPrice { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Interactive series only: what happens when this scene ends.
    public SceneOutcome Outcome { get; set; }
    /// <summary>The question above the options (选择提示), e.g. 「门外是消失三年的前任，你要？」.</summary>
    public string? ChoicePrompt { get; set; }
    /// <summary>The options appear this many seconds before the scene ends (0 = when it ends, holding the last frame).</summary>
    public int ChoiceLeadSeconds { get; set; }
    /// <summary>Seconds the viewer has to choose before the default option plays; null = the options wait.</summary>
    public int? ChoiceCountdown { get; set; }
    /// <summary>The scene that plays next when <see cref="Outcome"/> is Continue.</summary>
    public long? NextEpisodeId { get; set; }
    /// <summary>The ending's name (结局「真相」) when <see cref="Outcome"/> is Ending.</summary>
    public string? EndingTitle { get; set; }
    public List<StoryOption> Options { get; set; } = [];
}
