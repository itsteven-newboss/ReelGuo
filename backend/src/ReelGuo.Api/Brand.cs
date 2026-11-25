using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ReelGuo.Api;

/// <summary>
/// Which site this API serves (one API per site; each has its own database). The web app gets the same from
/// web/sites/&lt;key&gt;/site.json at build time; the staging scripts write both. "ReelGuo" stays where software reads it,
/// not people: <c>app_name</c> in /api/v1/config (the desktop and Android apps check it before trusting a server), the
/// <c>ReelGuoApp/</c> user agent, headers and code names.
/// </summary>
public sealed class BrandOptions
{
    public const string Section = "Brand";

    /// <summary>The site's code name, as in web/sites/&lt;key&gt; (its iPhone icon is Apps/WebClip/&lt;key&gt;.png).</summary>
    public string Key { get; set; } = "huangyue";

    /// <summary>The name visitors see.</summary>
    public string Name { get; set; } = "黄月短剧";

    /// <summary>Short form inside compound labels: 黄月原创, 黄月APP. The full name where the language has none.</summary>
    public string Short { get; set; } = "黄月";

    /// <summary>The viewer site's language (Sites/Texts/&lt;locale&gt;.json and Legal/Texts/&lt;locale&gt;/): zh-CN, id, vi, pt-BR.</summary>
    public string Locale { get; set; } = "zh-CN";
}

/// <summary>The site's name and the text the server writes for viewers, in the site's language. A singleton.</summary>
public sealed class SiteBrand
{
    public SiteBrand(IOptions<BrandOptions> options) : this(options.Value) { }

    public SiteBrand(BrandOptions o)
    {
        Key = string.IsNullOrWhiteSpace(o.Key) ? "huangyue" : o.Key.Trim();
        Name = string.IsNullOrWhiteSpace(o.Name) ? Key : o.Name.Trim();
        Short = string.IsNullOrWhiteSpace(o.Short) ? Name : o.Short.Trim();
        Locale = SiteText.Supported(o.Locale) ? o.Locale.Trim() : throw new InvalidOperationException(
            $"Brand:Locale '{o.Locale}' has no Sites/Texts/<locale>.json; use one of {string.Join(", ", SiteText.Locales)}");
        Text = SiteText.For(Locale);
    }

    /// <summary>黄月短剧 as it has always been: used where no configuration is at hand (tests, tools).</summary>
    public static readonly SiteBrand Default = new(new BrandOptions());

    public string Key { get; }
    public string Name { get; }
    public string Short { get; }
    public string Locale { get; }
    public SiteText Text { get; }

    /// <summary>Whether the site is the Chinese one (its seeded categories and products need no translating).</summary>
    public bool Chinese => Locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>A text with {brand} and {short} filled in, plus any other values.</summary>
    public string T(string key, params (string Name, object? Value)[] values) =>
        Text.Get(key, [("brand", Name), ("short", Short), .. values]);
}

/// <summary>
/// The server's viewer-facing text in one language (Sites/Texts/&lt;locale&gt;.json, embedded): search-engine pages, the
/// iPhone profile, legal page names, wallet entries, default categories and products. Keys are dotted names;
/// "{name}" placeholders are filled from the values given. Every language has the same keys (tests check it).
/// </summary>
public sealed class SiteText
{
    private static readonly ConcurrentDictionary<string, SiteText> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The languages with a text file.</summary>
    public static readonly IReadOnlyList<string> Locales = ["zh-CN", "id", "vi", "pt-BR"];

    public static bool Supported(string? locale) => locale is not null && Locales.Contains(locale.Trim(), StringComparer.OrdinalIgnoreCase);

    public static SiteText For(string locale) => Cache.GetOrAdd(locale.Trim(), l =>
    {
        var name = Locales.First(x => string.Equals(x, l, StringComparison.OrdinalIgnoreCase));
        using var stream = typeof(SiteText).Assembly.GetManifestResourceStream($"texts/{name}.json")
            ?? throw new InvalidOperationException($"Sites/Texts/{name}.json is missing from the build");
        using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        var lists = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.Array) lists[p.Name] = [.. p.Value.EnumerateArray().Select(x => x.GetString() ?? "")];
            else strings[p.Name] = p.Value.GetString() ?? "";
        }
        return new SiteText(name, strings, lists);
    });

    private readonly IReadOnlyDictionary<string, string> _strings;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _lists;

    private SiteText(string locale, IReadOnlyDictionary<string, string> strings, IReadOnlyDictionary<string, IReadOnlyList<string>> lists)
    {
        Locale = locale;
        _strings = strings;
        _lists = lists;
    }

    public string Locale { get; }

    public IEnumerable<string> Keys => _strings.Keys.Concat(_lists.Keys);

    public string this[string key] => _strings.TryGetValue(key, out var v) ? v
        : throw new KeyNotFoundException($"Sites/Texts/{Locale}.json has no \"{key}\"");

    public IReadOnlyList<string> List(string key) => _lists.TryGetValue(key, out var v) ? v
        : throw new KeyNotFoundException($"Sites/Texts/{Locale}.json has no list \"{key}\"");

    /// <summary>The text with each "{name}" replaced by its value (numbers as plain digits).</summary>
    public string Get(string key, params (string Name, object? Value)[] values)
    {
        var text = this[key];
        if (values.Length == 0 || !text.Contains('{')) return text;
        var b = new StringBuilder(text);
        foreach (var (name, value) in values)
            b.Replace("{" + name + "}", Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
        return b.ToString();
    }

    /// <summary>The "{name}" placeholders a text uses (for the tests that compare languages).</summary>
    public IEnumerable<string> Placeholders(string key) =>
        System.Text.RegularExpressions.Regex.Matches(this[key], @"\{([a-zA-Z]+)\}").Select(m => m.Groups[1].Value).Distinct();
}
