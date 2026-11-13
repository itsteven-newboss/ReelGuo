using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace ReelGuo.Api.Seo;

/// <summary>Search-engine pages: where the built web app's HTML comes from, and whether the site may be indexed.</summary>
public sealed class SeoOptions
{
    public const string Section = "Seo";

    /// <summary>The built web app's index.html on disk (used when set).</summary>
    public string TemplatePath { get; set; } = "";

    /// <summary>Otherwise fetched from the static web server, e.g. http://web:8080/index.html (Docker) — never a page route.</summary>
    public string TemplateUrl { get; set; } = "";

    /// <summary>
    /// Search engines may index the site. Off by default, so staging and test sites stay out of search results
    /// (robots.txt disallows everything and every page says noindex). Turn on for production only.
    /// </summary>
    public bool AllowIndexing { get; set; }

    /// <summary>Public origin for canonical links and the sitemap, e.g. https://reelguo.com. Empty: the request's origin.</summary>
    public string PublicBaseUrl { get; set; } = "";

    /// <summary>Country whose licences decide which series the sitemap lists (search engines crawl mostly from the US).</summary>
    public string SitemapCountry { get; set; } = "US";

    /// <summary>How long rendered pages and the template are reused.</summary>
    public int CacheSeconds { get; set; } = 60;
}

/// <summary>What one page puts into the template.</summary>
public sealed record SeoPage(
    int Status,
    string Title,
    string Description,
    string? CanonicalPath,
    bool Index,
    string Body,
    string? Image = null,
    string OgType = "website",
    IReadOnlyList<object>? JsonLd = null);

/// <summary>
/// The web app's index.html, fetched once and reused for <see cref="SeoOptions.CacheSeconds"/>; on a failed refresh
/// the last good copy is kept. <see cref="Render"/> fills in the page's head tags and readable content.
/// </summary>
public sealed partial class PageTemplate(IOptions<SeoOptions> options, IHttpClientFactory http, TimeProvider time, ILogger<PageTemplate> logger)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _html;
    private DateTimeOffset _loadedAt;

    public bool Configured => !string.IsNullOrWhiteSpace(options.Value.TemplatePath) || !string.IsNullOrWhiteSpace(options.Value.TemplateUrl);

    private bool Fresh => _html is not null && time.GetUtcNow() - _loadedAt < TimeSpan.FromSeconds(Math.Max(5, options.Value.CacheSeconds));

    public async Task<string?> GetAsync(CancellationToken ct)
    {
        if (!Configured) return null;
        if (Fresh) return _html;
        await _lock.WaitAsync(ct);
        try
        {
            if (Fresh) return _html;
            var o = options.Value;
            var html = !string.IsNullOrWhiteSpace(o.TemplatePath)
                ? await File.ReadAllTextAsync(o.TemplatePath, Encoding.UTF8, ct)
                : await http.CreateClient("seo-template").GetStringAsync(o.TemplateUrl, ct);
            if (!html.Contains(RootDiv, StringComparison.Ordinal)) throw new InvalidDataException("The page template has no <div id=\"root\"></div>.");
            _html = html;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not load the page template; {State}.", _html is null ? "pages cannot be rendered yet" : "keeping the last good copy");
        }
        finally
        {
            _loadedAt = time.GetUtcNow();
            _lock.Release();
        }
        return _html;
    }

    private const string RootDiv = "<div id=\"root\"></div>";

    /// <summary>Puts the page into the template: title, description, robots, canonical, Open Graph, JSON-LD, and the content.</summary>
    public static string Render(string template, SeoPage page, string baseUrl, bool allowIndexing)
    {
        var head = new StringBuilder();
        head.Append("<title>").Append(Html.Text(page.Title)).Append("</title>\n");
        head.Append("    <meta name=\"description\" content=\"").Append(Html.Attr(page.Description)).Append("\" />\n");
        head.Append("    <meta name=\"robots\" content=\"").Append(allowIndexing && page.Index ? "index,follow,max-image-preview:large" : "noindex,nofollow").Append("\" />\n");
        // RTA label: lets parental-control filters recognise the site as adults-only.
        head.Append("    <meta name=\"RATING\" content=\"RTA-5042-1996-1400-1577-RTA\" />\n");
        if (page.CanonicalPath is not null)
        {
            var url = baseUrl + page.CanonicalPath;
            head.Append("    <link rel=\"canonical\" href=\"").Append(Html.Attr(url)).Append("\" />\n");
            head.Append("    <meta property=\"og:url\" content=\"").Append(Html.Attr(url)).Append("\" />\n");
        }
        head.Append("    <meta property=\"og:site_name\" content=\"ReelGuo\" />\n");
        head.Append("    <meta property=\"og:type\" content=\"").Append(Html.Attr(page.OgType)).Append("\" />\n");
        head.Append("    <meta property=\"og:title\" content=\"").Append(Html.Attr(page.Title)).Append("\" />\n");
        head.Append("    <meta property=\"og:description\" content=\"").Append(Html.Attr(page.Description)).Append("\" />\n");
        head.Append("    <meta name=\"twitter:card\" content=\"").Append(page.Image is null ? "summary" : "summary_large_image").Append("\" />\n");
        if (page.Image is not null) head.Append("    <meta property=\"og:image\" content=\"").Append(Html.Attr(Absolute(page.Image, baseUrl))).Append("\" />\n");
        foreach (var block in page.JsonLd ?? [])
            head.Append("    <script type=\"application/ld+json\">").Append(Html.Json(block)).Append("</script>\n");

        // The template's own robots tag (noindex, for the plain static file) is replaced by this page's.
        var html = RobotsMeta().Replace(template, "");
        html = TitleTag().Replace(html, _ => head.ToString().TrimEnd(), 1);
        return html.Replace(RootDiv, $"<div id=\"seo\" class=\"rg-ssr\">{page.Body}</div>\n    {RootDiv}", StringComparison.Ordinal);
    }

    public static string Absolute(string url, string baseUrl) => url.StartsWith('/') ? baseUrl + url : url;

    [GeneratedRegex(@"<title>.*?</title>", RegexOptions.Singleline)]
    private static partial Regex TitleTag();

    [GeneratedRegex(@"\s*<meta\s+name=""robots""[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex RobotsMeta();
}
