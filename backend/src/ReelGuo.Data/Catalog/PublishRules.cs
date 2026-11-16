using ReelGuo.Data.Entities;
using ReelGuo.Data.Geo;

namespace ReelGuo.Data.Catalog;

/// <summary>
/// What must be true before a series can be published. Each problem is a zh-CN message for admin.
/// The rights fields (版权与合规) are optional: a new series gets <see cref="ApplyNewSeriesDefaults"/>, no licence start
/// means "available now" and no territories means worldwide. What is still checked: an open channel, a processed
/// episode, dates that make sense, and consent records when a real, identifiable person is marked as appearing.
/// </summary>
public static class PublishRules
{
    public const string Worldwide = "*";

    /// <summary>Rights holder of a new series when none is given.</summary>
    public const string DefaultRightsHolder = "huangyueai";

    public static List<string> Check(Series series, Category category, int readyEpisodes, DateOnly today)
    {
        var problems = new List<string>();

        if (category.Status != CategoryStatus.Active)
            problems.Add($"频道「{category.Name}」未开放，不能发布内容");
        if (readyEpisodes == 0)
            problems.Add("至少需要一集处理完成的视频");
        if (series.LicenceEnd is { } end && end < today)
            problems.Add("授权已过期");
        if (series.LicenceStart is { } start && series.LicenceEnd is { } finish && finish < start)
            problems.Add("授权结束日期早于开始日期");
        // Only when someone marks it: an identifiable real person in adult content needs their signed consent on file.
        if (series.HasRealPerson == true && string.IsNullOrWhiteSpace(series.ConsentRef))
            problems.Add("出现真实人物时必须填写授权同意书编号");

        return problems;
    }

    /// <summary>
    /// Defaults for a series being created (admin editor, folder upload, CSV import), for whatever was left empty:
    /// <see cref="DefaultRightsHolder"/>, worldwide, and "no" for the AI and real-person questions.
    /// </summary>
    public static void ApplyNewSeriesDefaults(Series series)
    {
        if (string.IsNullOrWhiteSpace(series.RightsHolder)) series.RightsHolder = DefaultRightsHolder;
        if (series.Territories.Count == 0) series.Territories = [Worldwide];
        series.AiGenerated ??= false;
        series.HasRealPerson ??= false;
    }

    /// <summary>Normalises a territory list: ISO codes upper-cased, "*" for worldwide. Returns null if any entry is invalid.</summary>
    public static List<string>? NormalizeTerritories(IEnumerable<string> raw)
    {
        var result = new List<string>();
        foreach (var item in raw.Select(t => t.Trim()).Where(t => t.Length > 0))
        {
            if (item == Worldwide) { result.Add(Worldwide); continue; }
            var code = CountryCodes.Normalize(item);
            if (code is null || !CountryCodes.Iso3166.Contains(code)) return null;
            result.Add(code);
        }
        return result.Contains(Worldwide) ? [Worldwide] : result.Distinct().Order().ToList();
    }
}
