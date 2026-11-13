namespace ReelGuo.Data.Entities;

/// <summary>专题: a curated collection of series (e.g. 精品高分短剧专辑), shown on the home page and the 专题 page.</summary>
public sealed class Topic
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public string Description { get; set; } = "";
    public bool Published { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<TopicSeries> Items { get; set; } = [];
}

public sealed class TopicSeries
{
    public int TopicId { get; set; }
    public long SeriesId { get; set; }
    public Series Series { get; set; } = null!;
    /// <summary>Order within the topic; the first visible series also supplies the topic's cover.</summary>
    public int Position { get; set; }
}

/// <summary>公告: a line in the home page's announcement bar (new address, notices). Optional link.</summary>
public sealed class Announcement
{
    public int Id { get; set; }
    public required string Text { get; set; }
    public string? Url { get; set; }
    public bool Active { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A viewer's 1-10 rating of a series (点我评分). One per viewer; rating again replaces it.</summary>
public sealed class SeriesRating
{
    public long UserId { get; set; }
    public long SeriesId { get; set; }
    public int Score { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
