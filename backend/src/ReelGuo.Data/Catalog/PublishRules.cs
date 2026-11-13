using ReelGuo.Data.Entities;
using ReelGuo.Data.Geo;

namespace ReelGuo.Data.Catalog;

/// <summary>What must be true before a series can be published. Each problem is a zh-CN message for admin.</summary>
public static class PublishRules
{
    public const string Worldwide = "*";

    public static List<string> Check(Series series, Category category, int readyEpisodes, DateOnly today)
    {
        var problems = new List<string>();

        if (category.Status != CategoryStatus.Active)
            problems.Add($"频道「{category.Name}」未开放，不能发布内容");
        if (readyEpisodes == 0)
            problems.Add("至少需要一集处理完成的视频");
        if (string.IsNullOrWhiteSpace(series.RightsHolder))
            problems.Add("未填写版权方");
        if (string.IsNullOrWhiteSpace(series.ContractRef))
            problems.Add("未填写合同编号");
        if (series.LicenceStart is null)
            problems.Add("未填写授权开始日期");
        if (series.LicenceEnd is { } end && end < today)
            problems.Add("授权已过期");
        if (series.LicenceStart is { } start && series.LicenceEnd is { } finish && finish < start)
            problems.Add("授权结束日期早于开始日期");
        if (series.Territories.Count == 0)
            problems.Add("未填写授权地区");
        if (series.AiGenerated is null)
            problems.Add("未声明是否为 AI 生成内容");
        if (series.HasRealPerson is null)
            problems.Add("未声明是否出现真实人物");
        if (series.HasRealPerson == true && string.IsNullOrWhiteSpace(series.ConsentRef))
            problems.Add("出现真实人物时必须填写授权同意书编号");

        return problems;
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
