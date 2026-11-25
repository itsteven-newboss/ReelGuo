using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ReelGuo.Api.Geo;
using ReelGuo.Data.Media;
using ReelGuo.Api.Public;
using ReelGuo.Data;
using ReelGuo.Data.Content;
using ReelGuo.Data.Entities;

namespace ReelGuo.Api.Seo;

/// <summary>HTML and JSON escaping for server-rendered pages.</summary>
public static class Html
{
    /// <summary>Escapes the five HTML-special characters; everything else stays as written (the pages are UTF-8, so é, « and © need no entities).</summary>
    public static string Text(string? s) => string.IsNullOrEmpty(s) ? "" : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
    public static string Attr(string? s) => Text(s);
    public static string Url(string s) => Uri.EscapeDataString(s);

    /// <summary>JSON for a &lt;script type="application/ld+json"&gt; block; "&lt;/" is escaped so it cannot close the tag.</summary>
    public static string Json(object value) =>
        JsonSerializer.Serialize(value, JsonOptions).Replace("</", "<\\/", StringComparison.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Clip(string? s, int max)
    {
        var t = (s ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return t.Length <= max ? t : t[..max].TrimEnd() + "…";
    }
}

/// <summary>
/// Pages for search engines. Every page route of the web app is answered here (Caddy sends page paths to the API,
/// files stay static): the app's index.html with the page's title, description, canonical link, Open Graph, JSON-LD
/// and its readable content in <c>#seo</c>. The browser app then takes over as before. Content follows the same rules as
/// the API: licence territories and dates, the country block list, and strict age-verification countries.
/// </summary>
public static class SeoEndpoints
{
    private const int ChannelPageSize = 42;
    private const int PostPageSize = 12;
    private const int SitemapSeriesPerFile = 400;

    private static readonly (string Kind, RankKind Rank, string TextKey)[] Boards =
        [("hot", RankKind.Hot, "ranks.hot"), ("recommend", RankKind.Recommend, "ranks.recommend"), ("potential", RankKind.Potential, "ranks.potential")];

    private static readonly IReadOnlyList<ReelGuo.Api.Legal.LegalPages.Page> LegalPages = ReelGuo.Api.Legal.LegalPages.All;

    private static readonly string[] PrivatePages = ["/search", "/me", "/me/wallet", "/store", "/pay/result", "/settings"];

    /// <summary>Sections of the app download landing page, each with its own URL (/app/genres, …).</summary>
    private static readonly HashSet<string> AppSections = ["home", "highlights", "genres", "experience", "wishlist", "creator", "about"];

    public static void MapSeo(this WebApplication app)
    {
        var pages = app.MapGroup("").ExcludeFromDescription();
        string[] methods = ["GET", "HEAD"];
        pages.MapMethods("/robots.txt", methods, Robots);
        pages.MapMethods("/sitemap.xml", methods, SitemapIndex);
        pages.MapMethods("/sitemaps/{name}", methods, SitemapFile);

        pages.MapMethods("/", methods, (HttpContext h, Ctx c, CancellationToken ct) => Serve(h, c, ct, (country, ct2) => Home(c, country, ct2)));
        pages.MapMethods("/c/{slug}", methods, (string slug, HttpContext h, Ctx c, CancellationToken ct) => Serve(h, c, ct, (country, ct2) => Channel(c, h, slug, country, ct2)));
        pages.MapMethods("/video/{id:long}", methods, (long id, HttpContext h, Ctx c, CancellationToken ct) =>
            Serve(h, c, ct, (country, ct2) => Series(c, id, int.TryParse(h.Request.Query["ep"], out var ep) ? ep : null, country, ct2)));
        pages.MapMethods("/video/{id:long}/ep-{ep:int}", methods, (long id, int ep, HttpContext h, Ctx c, CancellationToken ct) =>
            Serve(h, c, ct, (country, ct2) => Series(c, id, ep, country, ct2)));
        pages.MapMethods("/ranks", methods, (HttpContext h, Ctx c, CancellationToken ct) => Serve(h, c, ct, (country, ct2) => Ranks(c, "hot", country, ct2)));
        pages.MapMethods("/ranks/{kind}", methods, (string kind, HttpContext h, Ctx c, CancellationToken ct) => Serve(h, c, ct, (country, ct2) => Ranks(c, kind, country, ct2)));
        pages.MapMethods("/topics", methods, (HttpContext h, Ctx c, CancellationToken ct) => Serve(h, c, ct, (country, ct2) => Topics(c, country, ct2)));
        pages.MapMethods("/topics/{id:int}", methods, (int id, HttpContext h, Ctx c, CancellationToken ct) => Serve(h, c, ct, (country, ct2) => Topic(c, id, country, ct2)));
        pages.MapMethods("/posts", methods, (HttpContext h, Ctx c, CancellationToken ct) => Serve(h, c, ct, (country, ct2) => Posts(c, h, ct2)));
        pages.MapMethods("/post/{id:long}", methods, (long id, HttpContext h, Ctx c, CancellationToken ct) => Serve(h, c, ct, (country, ct2) => Post(c, id, country, ct2)));
        pages.MapMethods("/go-home", methods, (HttpContext h, Ctx c, CancellationToken ct) => Serve(h, c, ct, (_, ct2) => WayHome(c, ct2)));
        // The app download landing page: /app/{section} (one page, its sections share the /app/home canonical).
        // /download was its address before.
        pages.MapMethods("/download", methods, () => Results.Redirect("/app/home", permanent: true));
        pages.MapMethods("/app", methods, (HttpContext h, Ctx c, CancellationToken ct) => Serve(h, c, ct, (_, ct2) => Download(c, ct2)));
        pages.MapMethods("/app/{section}", methods, (string section, HttpContext h, Ctx c, CancellationToken ct) =>
            Serve(h, c, ct, (_, ct2) => AppSections.Contains(section) ? Download(c, ct2) : NotFound(c, ct2)));
        foreach (var legal in LegalPages)
            pages.MapMethods(legal.Path, methods, (HttpContext h, Ctx c, CancellationToken ct) => Serve(h, c, ct, (_, ct2) => Legal(c, legal, ct2), legal: true));
        foreach (var path in PrivatePages)
            pages.MapMethods(path, methods, (HttpContext h, Ctx c, CancellationToken ct) => Serve(h, c, ct, (_, ct2) => Private(c, ct2)));

        // Any other page path: the app's own "not found" page, with a 404 status. API, media and health paths keep their 404s.
        app.MapFallback(async (HttpContext h, Ctx c, CancellationToken ct) =>
        {
            var path = h.Request.Path;
            if (!HttpMethods.IsGet(h.Request.Method) && !HttpMethods.IsHead(h.Request.Method)
                || path.StartsWithSegments("/api") || path.StartsWithSegments("/media") || path.StartsWithSegments("/health")
                || path.StartsWithSegments("/openapi") || path.StartsWithSegments("/scalar"))
                return Results.NotFound();
            return await Serve(h, c, ct, (_, ct2) => NotFound(c, ct2));
        }).ExcludeFromDescription();
    }

    /// <summary>Everything a page renderer needs, injected once.</summary>
    public sealed class Ctx(ReelGuoDbContext db, MediaUrlSigner signer, PageTemplate template, IOptions<SeoOptions> seo, IOptions<SiteOptions> site,
        IOptions<AppOptions> app, ICountryResolver countries, IBlockedCountryStore blocked, IAgeRuleStore ages, IMemoryCache cache, TimeProvider time,
        PostRenderer renderer, SiteBrand brand)
    {
        public ReelGuoDbContext Db { get; } = db;
        public MediaUrlSigner Signer { get; } = signer;
        public PageTemplate Template { get; } = template;
        public SeoOptions Seo { get; } = seo.Value;
        public SiteOptions Site { get; } = site.Value;
        public AppOptions App { get; } = app.Value;
        public ICountryResolver Countries { get; } = countries;
        public IBlockedCountryStore Blocked { get; } = blocked;
        public IAgeRuleStore Ages { get; } = ages;
        public IMemoryCache Cache { get; } = cache;
        public TimeProvider Time { get; } = time;
        public PostRenderer Renderer { get; } = renderer;
        public SiteBrand Brand { get; } = brand;
        /// <summary>The page text in the site's language, {brand} and {short} filled in.</summary>
        public string T(string key, params (string Name, object? Value)[] values) => Brand.T(key, values);
        /// <summary>The configured tagline, else the language's default.</summary>
        public string Tagline => string.IsNullOrWhiteSpace(Site.Tagline) ? T("site.tagline") : Site.Tagline;
        public DateOnly Today => PublicEndpoints.Today(Time);
        public IQueryable<Series> Visible(string country) => Db.Series.AsNoTracking().VisibleTo(country, Today);
    }

    // ---------------------------------------------------------------------------------------------
    // Serving
    // ---------------------------------------------------------------------------------------------

    private static async Task<IResult> Serve(HttpContext http, Ctx c, CancellationToken ct, Func<string, CancellationToken, Task<SeoPage>> build, bool legal = false)
    {
        var template = await c.Template.GetAsync(ct);
        if (template is null) return Results.NotFound();
        var baseUrl = BaseUrl(c, http);
        var country = c.Countries.Resolve(http);

        // Blocked countries and strict age-verification countries get the app without content (the app explains why).
        SeoPage page;
        if (legal)
            page = await build(country, ct);   // small, and an admin's 法务审核 switch should show at once
        else if ((await c.Blocked.GetAsync(ct)).Contains(country))
            page = new SeoPage(StatusCodes.Status451UnavailableForLegalReasons, c.Brand.Name, c.T("site.region"), null, false, "");
        else if (await c.Ages.ModeForAsync(country, ct) == AgeCheckMode.Strict)
            page = new SeoPage(StatusCodes.Status200OK, c.Brand.Name, c.T("site.ageRequired"), null, false, "");
        else
        {
            var key = $"seo|{country}|{http.Request.Path}{http.Request.QueryString}";
            page = (await c.Cache.GetOrCreateAsync(key, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(Math.Max(5, c.Seo.CacheSeconds));
                return await build(country, ct);
            }))!;
        }

        var html = PageTemplate.Render(template, page, baseUrl, c.Seo.AllowIndexing, c.Brand.Name);
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers.Vary = "Accept-Encoding";
        if (!c.Seo.AllowIndexing) http.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8, page.Status);
    }

    private static string BaseUrl(Ctx c, HttpContext http) =>
        string.IsNullOrWhiteSpace(c.Seo.PublicBaseUrl) ? $"{http.Request.Scheme}://{http.Request.Host}" : c.Seo.PublicBaseUrl.TrimEnd('/');

    // ---------------------------------------------------------------------------------------------
    // Shared pieces
    // ---------------------------------------------------------------------------------------------

    private static async Task<List<Category>> ChannelsAsync(Ctx c, CancellationToken ct) =>
        await c.Db.Categories.AsNoTracking().Where(x => x.Status != CategoryStatus.Hidden).OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(ct);

    /// <summary>Site navigation, the 18+ notice for visitors without JavaScript, the page, and the footer links.</summary>
    private static async Task<string> FrameAsync(Ctx c, IEnumerable<(string Label, string? Href)> crumbs, string main, CancellationToken ct)
    {
        var channels = await ChannelsAsync(c, ct);
        var b = new StringBuilder();
        b.Append($"<header><a href=\"/\"><strong>{Html.Text(c.Brand.Name)}</strong></a> <nav aria-label=\"{Html.Attr(c.T("nav.label"))}\"><a href=\"/\">{Html.Text(c.T("nav.home"))}</a>");
        b.Append($" <a href=\"/c/all?sort=original\">{Html.Text(c.T("nav.original"))}</a>");
        foreach (var ch in channels.Where(x => x.Status == CategoryStatus.Active))
            b.Append(" <a href=\"/c/").Append(Html.Url(ch.Slug)).Append("\">").Append(Html.Text(ch.Name)).Append("</a>");
        b.Append($" <a href=\"/topics\">{Html.Text(c.T("nav.topics"))}</a> <a href=\"/ranks\">{Html.Text(c.T("nav.ranks"))}</a> <a href=\"/posts\">{Html.Text(c.T("nav.blog"))}</a>");
        b.Append($" <a href=\"/go-home\">{Html.Text(c.T("nav.wayHome"))}</a> <a href=\"/app/home\">{Html.Text(c.T("nav.app"))}</a></nav></header>\n");
        b.Append($"<noscript><p><strong>{Html.Text(c.T("nav.noscript"))}</strong></p></noscript>\n");
        var list = crumbs.ToList();
        if (list.Count > 1)
        {
            b.Append($"<nav aria-label=\"{Html.Attr(c.T("nav.crumbs"))}\" class=\"crumbs\">");
            for (var i = 0; i < list.Count; i++)
            {
                if (i > 0) b.Append(" / ");
                var (label, href) = list[i];
                b.Append(href is null ? Html.Text(label) : $"<a href=\"{Html.Attr(href)}\">{Html.Text(label)}</a>");
            }
            b.Append("</nav>\n");
        }
        b.Append("<main>").Append(main).Append("</main>\n");
        b.Append($"<footer><nav aria-label=\"{Html.Attr(c.T("footer.label"))}\"><a href=\"/go-home\">{Html.Text(c.T("nav.wayHome"))}</a> <a href=\"/app/home\">{Html.Text(c.T("footer.download"))}</a>");
        b.Append($" <a href=\"/c/all\">{Html.Text(c.T("footer.latest"))}</a> <a href=\"/ranks\">{Html.Text(c.T("footer.hot"))}</a> <a href=\"/topics\">{Html.Text(c.T("footer.topics"))}</a>");
        foreach (var legal in LegalPages) b.Append(" <a href=\"").Append(legal.Path).Append("\">").Append(Html.Text(legal.Title(c.Brand))).Append("</a>");
        b.Append($"</nav><p>{Html.Text(c.T("footer.rights"))}</p></footer>");
        return b.ToString();
    }

    private static string SeriesPath(long id, int? ep = null) => ep is > 1 ? $"/video/{id}/ep-{ep}" : $"/video/{id}";

    private static string ProgressLabel(Ctx c, Series s) =>
        s.IsFinished ? c.T("progress.all", ("count", Math.Max(s.EpisodeCount, s.TotalEpisodes))) : c.T("progress.upTo", ("count", s.EpisodeCount));

    private static string EpisodeName(Ctx c, int n) => c.T("series.ep", ("n", n));

    /// <summary>A list of series: title link, channel, progress, tags, synopsis.</summary>
    private static string SeriesList(Ctx c, IEnumerable<Series> items, bool ordered = false)
    {
        var b = new StringBuilder(ordered ? "<ol class=\"cards\">" : "<ul class=\"cards\">");
        foreach (var s in items)
        {
            b.Append("<li><a href=\"").Append(SeriesPath(s.Id)).Append("\">").Append(Html.Text(s.Title)).Append("</a>");
            b.Append(" <small>").Append(Html.Text(s.Category?.Name ?? "")).Append(" · ").Append(Html.Text(ProgressLabel(c, s)));
            if (s.Score > 0) b.Append(" · ").Append(Html.Text(c.T("score", ("score", s.Score.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)))));
            if (s.Tags.Count > 0) b.Append(" · ").Append(Html.Text(string.Join(" / ", s.Tags.Take(4))));
            b.Append("</small>");
            if (!string.IsNullOrWhiteSpace(s.Description)) b.Append("<p>").Append(Html.Text(Html.Clip(s.Description, 120))).Append("</p>");
            b.Append("</li>");
        }
        return b.Append(ordered ? "</ol>" : "</ul>").ToString();
    }

    private static object Breadcrumbs(string baseUrl, IEnumerable<(string Label, string? Href)> crumbs) => new Dictionary<string, object>
    {
        ["@context"] = "https://schema.org",
        ["@type"] = "BreadcrumbList",
        ["itemListElement"] = crumbs.Where(x => x.Href is not null).Select((x, i) => new Dictionary<string, object>
        {
            ["@type"] = "ListItem", ["position"] = i + 1, ["name"] = x.Label, ["item"] = baseUrl + x.Href,
        }).ToList(),
    };

    // ---------------------------------------------------------------------------------------------
    // Pages
    // ---------------------------------------------------------------------------------------------

    private static async Task<SeoPage> Home(Ctx c, string country, CancellationToken ct)
    {
        var visible = c.Visible(country).Include(s => s.Category);
        var picks = await visible.OrderByDescending(s => s.RecommendWeight).ThenByDescending(s => s.HotScore).Take(14).ToListAsync(ct);
        var latest = await visible.OrderByDescending(s => s.LastEpisodeAt ?? s.PublishedAt).ThenByDescending(s => s.Id).Take(14).ToListAsync(ct);
        var hot = await visible.OrderByDescending(s => s.HotScore).ThenByDescending(s => s.PlayCount).Take(14).ToListAsync(ct);
        var topics = await c.Db.Topics.AsNoTracking().Where(t => t.Published).OrderBy(t => t.SortOrder).ThenByDescending(t => t.Id).Take(6).ToListAsync(ct);

        var m = new StringBuilder();
        m.Append($"<h1>{Html.Text(c.Brand.Name)} · ").Append(Html.Text(c.Tagline)).Append("</h1>");
        m.Append($"<p>{Html.Text(c.T("home.intro"))}</p>");
        m.Append($"<section><h2>{Html.Text(c.T("home.picks"))}</h2>").Append(SeriesList(c, picks)).Append("</section>");
        m.Append($"<section><h2>{Html.Text(c.T("home.latest"))}</h2>").Append(SeriesList(c, latest)).Append($"<p><a href=\"/c/all\">{Html.Text(c.T("home.allLatest"))}</a></p></section>");
        m.Append($"<section><h2>{Html.Text(c.T("home.hot"))}</h2>").Append(SeriesList(c, hot)).Append($"<p><a href=\"/ranks/hot\">{Html.Text(c.T("home.moreHot"))}</a></p></section>");
        if (topics.Count > 0)
        {
            m.Append($"<section><h2>{Html.Text(c.T("home.topics"))}</h2><ul>");
            foreach (var t in topics) m.Append("<li><a href=\"/topics/").Append(t.Id).Append("\">").Append(Html.Text(t.Title)).Append("</a> <small>").Append(Html.Text(Html.Clip(t.Description, 80))).Append("</small></li>");
            m.Append("</ul></section>");
        }
        var body = await FrameAsync(c, [], m.ToString(), ct);
        var site = new Dictionary<string, object>
        {
            ["@context"] = "https://schema.org", ["@type"] = "WebSite", ["name"] = c.Brand.Name, ["url"] = BaseFor(c) + "/", ["inLanguage"] = c.Brand.Locale,
            ["potentialAction"] = new Dictionary<string, object>
            {
                ["@type"] = "SearchAction", ["target"] = BaseFor(c) + "/search?q={search_term_string}", ["query-input"] = "required name=search_term_string",
            },
        };
        return new SeoPage(200, c.T("home.title", ("tagline", c.Tagline)), c.T("home.description", ("tagline", c.Tagline)), "/", true, body,
            picks.FirstOrDefault()?.CoverKey is { } k ? c.Signer.PublicUrlOrNull(k) : null, JsonLd: [site]);
    }

    private static async Task<SeoPage> Channel(Ctx c, HttpContext http, string slug, string country, CancellationToken ct)
    {
        var page = Math.Max(1, int.TryParse(http.Request.Query["page"], out var p) ? p : 1);
        var sort = http.Request.Query["sort"].ToString();
        var original = sort == "original";
        var all = slug == "all";
        Category? channel = null;
        if (!all)
        {
            channel = await c.Db.Categories.AsNoTracking().FirstOrDefaultAsync(x => x.Slug == slug && x.Status != CategoryStatus.Hidden, ct);
            if (channel is null) return await NotFound(c, ct);
        }
        var name = original ? c.T("nav.original") : all ? c.T("channel.all") : channel!.Name;
        var intro = channel?.Description is { Length: > 0 } d ? d : c.T("channel.intro");
        var canonical = $"/c/{Html.Url(slug)}" + (original ? "?sort=original" : "") + (page > 1 ? (original ? "&" : "?") + $"page={page}" : "");
        var crumbs = new List<(string, string?)> { (c.T("nav.home"), "/"), (name, canonical) };

        if (channel?.Status == CategoryStatus.ComingSoon)
        {
            var soon = await FrameAsync(c, crumbs, $"<h1>{Html.Text(name)}</h1><p>{Html.Text(c.T("channel.soon"))}</p>", ct);
            return new SeoPage(200, c.T("channel.soonTitle", ("name", name)), intro, canonical, false, soon);
        }

        var q = c.Visible(country).Include(s => s.Category).AsQueryable();
        if (!all) q = q.Where(s => s.CategoryId == channel!.Id);
        if (original) q = q.Where(s => s.IsOriginal);
        var total = await q.CountAsync(ct);
        var pages = Math.Max(1, (int)Math.Ceiling(total / (double)ChannelPageSize));
        if (page > pages) return await NotFound(c, ct);
        var items = await q.OrderByDescending(s => s.LastEpisodeAt ?? s.PublishedAt).ThenByDescending(s => s.Id)
            .Skip((page - 1) * ChannelPageSize).Take(ChannelPageSize).ToListAsync(ct);

        var m = new StringBuilder();
        m.Append("<h1>").Append(Html.Text(c.T("channel.heading", ("name", name)))).Append("</h1><p>")
            .Append(Html.Text(c.T("channel.lead", ("name", name), ("intro", intro), ("total", total)))).Append("</p>");
        m.Append(SeriesList(c, items));
        var basePath = $"/c/{Html.Url(slug)}" + (original ? "?sort=original" : "");
        var join = original ? "&" : "?";
        m.Append(Pager(c, page, pages, n => n == 1 ? basePath : $"{basePath}{join}page={n}"));
        var body = await FrameAsync(c, crumbs, m.ToString(), ct);
        var title = page > 1 ? c.T("channel.titlePaged", ("name", name), ("page", page)) : c.T("channel.title", ("name", name));
        return new SeoPage(200, title, c.T("channel.description", ("name", name), ("intro", intro)), canonical, true, body, JsonLd: [Breadcrumbs(BaseFor(c), crumbs)]);
    }

    /// <summary>"Previous · page n of m · next" under a paged list.</summary>
    private static string Pager(Ctx c, int page, int pages, Func<int, string> href)
    {
        var m = new StringBuilder($"<nav aria-label=\"{Html.Attr(c.T("pager.label"))}\">");
        if (page > 1) m.Append("<a rel=\"prev\" href=\"").Append(href(page - 1)).Append("\">").Append(Html.Text(c.T("pager.prev"))).Append("</a> ");
        m.Append(Html.Text(c.T("pager.page", ("page", page), ("pages", pages))));
        if (page < pages) m.Append(" <a rel=\"next\" href=\"").Append(href(page + 1)).Append("\">").Append(Html.Text(c.T("pager.next"))).Append("</a>");
        return m.Append("</nav>").ToString();
    }

    private static async Task<SeoPage> Series(Ctx c, long id, int? ep, string country, CancellationToken ct)
    {
        var s = await c.Visible(country).Include(x => x.Category).Include(x => x.Author)
            .Include(x => x.Episodes.Where(e => e.Status == EpisodeStatus.Ready).OrderBy(e => e.EpisodeNumber))
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return await NotFound(c, ct);
        var episodes = s.Episodes.OrderBy(e => e.EpisodeNumber).ToList();
        Episode? current = null;
        if (ep is not null)
        {
            current = episodes.FirstOrDefault(e => e.EpisodeNumber == ep);
            if (current is null) return await NotFound(c, ct);
        }
        var related = await c.Visible(country).Include(x => x.Category).Where(x => x.CategoryId == s.CategoryId && x.Id != s.Id)
            .OrderByDescending(x => x.HotScore).Take(12).ToListAsync(ct);
        var rating = await PortalEndpoints.RatingAsync(c.Db, s.Id, ct);

        var episodeTitle = current is null || current.EpisodeNumber == 1 && ep is null ? null : EpisodeName(c, current.EpisodeNumber);
        var canonical = SeriesPath(s.Id, current?.EpisodeNumber);
        var crumbs = new List<(string, string?)> { (c.T("nav.home"), "/"), (s.Category.Name, $"/c/{Html.Url(s.Category.Slug)}"), (s.Title, SeriesPath(s.Id)) };
        if (episodeTitle is not null) crumbs.Add((episodeTitle, canonical));

        var m = new StringBuilder();
        m.Append("<article><h1>").Append(Html.Text(s.Title)).Append(episodeTitle is null ? "" : $" {episodeTitle}").Append("</h1>");
        // An interactive series' ending names stay hidden, as in the app.
        var spoiler = s.Kind == SeriesKind.Interactive && current?.Outcome == SceneOutcome.Ending;
        if (current is not null && !spoiler && !string.IsNullOrWhiteSpace(current.Title) && current.Title != episodeTitle)
            m.Append("<h2>").Append(Html.Text(current.Title)).Append("</h2>");
        m.Append("<p>").Append(Html.Text(string.IsNullOrWhiteSpace(s.Description) ? c.T("series.noSynopsis") : s.Description)).Append("</p>");
        m.Append($"<dl><dt>{Html.Text(c.T("series.channel"))}</dt><dd><a href=\"/c/").Append(Html.Url(s.Category.Slug)).Append("\">").Append(Html.Text(s.Category.Name)).Append("</a></dd>");
        m.Append($"<dt>{Html.Text(c.T("series.progress"))}</dt><dd>").Append(Html.Text(ProgressLabel(c, s))).Append("</dd>");
        if (s.PublishedAt is { } at) m.Append($"<dt>{Html.Text(c.T("series.published"))}</dt><dd>").Append(at.ToString("yyyy-MM-dd")).Append("</dd>");
        if (s.Score > 0) m.Append($"<dt>{Html.Text(c.T("series.score"))}</dt><dd>").Append(Html.Text(c.T("score", ("score", s.Score.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))))).Append("</dd>");
        if (s.Author is not null) m.Append($"<dt>{Html.Text(c.T("series.producer"))}</dt><dd>").Append(Html.Text(s.Author.Nickname)).Append("</dd>");
        if (s.Tags.Count > 0)
        {
            m.Append($"<dt>{Html.Text(c.T("series.tags"))}</dt><dd>");
            foreach (var t in s.Tags) m.Append("<a href=\"/search?q=").Append(Html.Url(t)).Append("\">").Append(Html.Text(t)).Append("</a> ");
            m.Append("</dd>");
        }
        m.Append("</dl>");
        if (episodes.Count > 0)
        {
            m.Append($"<section><h2>{Html.Text(c.T("series.episodes"))}</h2><ol class=\"episodes\">");
            foreach (var e in episodes)
                m.Append("<li><a href=\"").Append(e.EpisodeNumber == 1 ? SeriesPath(s.Id) : SeriesPath(s.Id, e.EpisodeNumber)).Append("\">")
                    .Append(Html.Text(EpisodeName(c, e.EpisodeNumber))).Append("</a></li>");
            m.Append("</ol></section>");
        }
        m.Append("</article>");
        if (related.Count > 0) m.Append($"<section><h2>{Html.Text(c.T("series.related"))}</h2>").Append(SeriesList(c, related)).Append("</section>");
        var body = await FrameAsync(c, crumbs, m.ToString(), ct);

        var baseUrl = BaseFor(c);
        var cover = c.Signer.PublicUrlOrNull(s.CoverKey);
        var show = new Dictionary<string, object>
        {
            ["@context"] = "https://schema.org", ["@type"] = "TVSeries", ["name"] = s.Title, ["url"] = baseUrl + SeriesPath(s.Id),
            ["description"] = Html.Clip(s.Description, 300), ["genre"] = new[] { s.Category.Name }.Concat(s.Tags).ToList(),
            ["numberOfEpisodes"] = Math.Max(s.TotalEpisodes, s.EpisodeCount), ["inLanguage"] = c.Brand.Locale, ["isFamilyFriendly"] = false,
            ["contentRating"] = "18+",
            ["episode"] = episodes.Take(100).Select(e => new Dictionary<string, object>
            {
                ["@type"] = "TVEpisode", ["episodeNumber"] = e.EpisodeNumber, ["name"] = EpisodeName(c, e.EpisodeNumber),
                ["url"] = baseUrl + (e.EpisodeNumber == 1 ? SeriesPath(s.Id) : SeriesPath(s.Id, e.EpisodeNumber)),
            }).ToList(),
        };
        if (cover is not null) show["image"] = PageTemplate.Absolute(cover, baseUrl);
        if (s.PublishedAt is { } published) show["datePublished"] = published.ToString("yyyy-MM-dd");
        if (rating.Count >= 5 && rating.Average is { } avg)
            show["aggregateRating"] = new Dictionary<string, object> { ["@type"] = "AggregateRating", ["ratingValue"] = Math.Round(avg, 1), ["bestRating"] = 10, ["worstRating"] = 1, ["ratingCount"] = rating.Count };

        var title = episodeTitle is null
            ? c.T("series.title", ("title", s.Title), ("channel", s.Category.Name))
            : c.T("series.titleEp", ("title", s.Title), ("ep", episodeTitle), ("channel", s.Category.Name));
        var description = c.T("series.description", ("synopsis", Html.Clip(s.Description, 130)), ("progress", ProgressLabel(c, s)), ("channel", s.Category.Name)).Trim();
        return new SeoPage(200, title, description, canonical, true, body, cover, episodeTitle is null ? "video.tv_show" : "video.episode",
            [show, Breadcrumbs(baseUrl, crumbs)]);
    }

    private static async Task<SeoPage> Ranks(Ctx c, string kind, string country, CancellationToken ct)
    {
        var board = Boards.FirstOrDefault(b => b.Kind == kind);
        if (board.Kind is null) return await NotFound(c, ct);
        var visible = c.Visible(country);
        // The board's order first, then the series with their channels (EF can't Include after projecting to Series).
        var order = await c.Db.Ranks.AsNoTracking().Where(r => r.Kind == board.Rank && visible.Any(s => s.Id == r.SeriesId))
            .OrderBy(r => r.Position).Take(20).Select(r => r.SeriesId).ToListAsync(ct);
        var found = await visible.Include(s => s.Category).Where(s => order.Contains(s.Id)).ToListAsync(ct);
        var rows = found.OrderBy(s => order.IndexOf(s.Id)).ToList();
        var boardName = c.T(board.TextKey);
        var crumbs = new List<(string, string?)> { (c.T("nav.home"), "/"), (c.T("nav.ranks"), "/ranks"), (boardName, $"/ranks/{board.Kind}") };
        var m = new StringBuilder($"<h1>{Html.Text(boardName)}</h1><p>{Html.Text(c.T("ranks.lead"))}</p>");
        m.Append("<nav>");
        foreach (var b in Boards) m.Append($"<a href=\"/ranks/{b.Kind}\">{Html.Text(c.T(b.TextKey))}</a> ");
        m.Append("</nav>").Append(SeriesList(c, rows, ordered: true));
        var body = await FrameAsync(c, crumbs, m.ToString(), ct);
        return new SeoPage(200, c.T("ranks.title", ("board", boardName)), c.T("ranks.description", ("board", boardName)),
            $"/ranks/{board.Kind}", true, body, JsonLd: [Breadcrumbs(BaseFor(c), crumbs)]);
    }

    private static async Task<SeoPage> Topics(Ctx c, string country, CancellationToken ct)
    {
        var topics = await c.Db.Topics.AsNoTracking().Where(t => t.Published).OrderBy(t => t.SortOrder).ThenByDescending(t => t.Id).Take(200).ToListAsync(ct);
        var crumbs = new List<(string, string?)> { (c.T("nav.home"), "/"), (c.T("nav.topics"), "/topics") };
        var m = new StringBuilder($"<h1>{Html.Text(c.T("topics.heading"))}</h1><p>{Html.Text(c.T("topics.lead"))}</p><ul>");
        foreach (var t in topics)
            m.Append("<li><a href=\"/topics/").Append(t.Id).Append("\">").Append(Html.Text(t.Title)).Append("</a> <small>").Append(Html.Text(Html.Clip(t.Description, 100))).Append("</small></li>");
        m.Append("</ul>");
        var body = await FrameAsync(c, crumbs, m.ToString(), ct);
        return new SeoPage(200, c.T("topics.title"), c.T("topics.description"), "/topics", true, body);
    }

    private static async Task<SeoPage> Topic(Ctx c, int id, string country, CancellationToken ct)
    {
        var topic = await c.Db.Topics.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && t.Published, ct);
        if (topic is null) return await NotFound(c, ct);
        var order = await c.Db.TopicSeries.AsNoTracking().Where(x => x.TopicId == id).OrderBy(x => x.Position).Select(x => x.SeriesId).ToListAsync(ct);
        var series = await c.Visible(country).Include(s => s.Category).Where(s => order.Contains(s.Id)).ToListAsync(ct);
        var items = series.OrderBy(s => order.IndexOf(s.Id)).ToList();
        var crumbs = new List<(string, string?)> { (c.T("nav.home"), "/"), (c.T("nav.topics"), "/topics"), (topic.Title, $"/topics/{id}") };
        var m = $"<h1>{Html.Text(topic.Title)}</h1><p>{Html.Text(c.T("topic.lead", ("description", topic.Description), ("count", items.Count)).Trim())}</p>{SeriesList(c, items)}";
        var body = await FrameAsync(c, crumbs, m, ct);
        return new SeoPage(200, c.T("topic.title", ("topic", topic.Title)),
            string.IsNullOrWhiteSpace(topic.Description) ? c.T("topic.description", ("topic", topic.Title)) : Html.Clip(topic.Description, 150),
            $"/topics/{id}", true, body, items.FirstOrDefault()?.CoverKey is { } k ? c.Signer.PublicUrlOrNull(k) : null, JsonLd: [Breadcrumbs(BaseFor(c), crumbs)]);
    }

    private static IQueryable<Post> PublishedPosts(Ctx c)
    {
        var now = c.Time.GetUtcNow();
        return c.Db.Posts.AsNoTracking().Where(p => p.Status == PostStatus.Published && p.PublishedAt <= now);
    }

    private static async Task<SeoPage> Posts(Ctx c, HttpContext http, CancellationToken ct)
    {
        var page = Math.Max(1, int.TryParse(http.Request.Query["page"], out var p) ? p : 1);
        var q = PublishedPosts(c);
        var total = await q.CountAsync(ct);
        var pages = Math.Max(1, (int)Math.Ceiling(total / (double)PostPageSize));
        if (page > pages) return await NotFound(c, ct);
        var posts = await q.OrderByDescending(x => x.PublishedAt).ThenByDescending(x => x.Id).Skip((page - 1) * PostPageSize).Take(PostPageSize).ToListAsync(ct);
        var crumbs = new List<(string, string?)> { (c.T("nav.home"), "/"), (c.T("nav.blog"), "/posts") };
        var m = new StringBuilder($"<h1>{Html.Text(c.T("nav.blog"))}</h1><p>{Html.Text(c.T("posts.lead"))}</p><ul>");
        foreach (var x in posts)
        {
            m.Append("<li><a href=\"/post/").Append(x.Id).Append("\">").Append(Html.Text(x.Title)).Append("</a>");
            if (x.PublishedAt is { } at) m.Append(" <small>").Append(at.ToString("yyyy-MM-dd")).Append("</small>");
            if (!string.IsNullOrWhiteSpace(x.Summary)) m.Append("<p>").Append(Html.Text(Html.Clip(x.Summary, 120))).Append("</p>");
            m.Append("</li>");
        }
        m.Append("</ul>").Append(Pager(c, page, pages, n => n == 1 ? "/posts" : $"/posts?page={n}"));
        var body = await FrameAsync(c, crumbs, m.ToString(), ct);
        return new SeoPage(200, page > 1 ? c.T("posts.titlePaged", ("page", page)) : c.T("posts.title"), c.T("posts.description"),
            page > 1 ? $"/posts?page={page}" : "/posts", true, body);
    }

    private static async Task<SeoPage> Post(Ctx c, long id, string country, CancellationToken ct)
    {
        var post = await PublishedPosts(c).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (post is null) return await NotFound(c, ct);
        Series? linked = post.SeriesId is { } sid ? await c.Visible(country).FirstOrDefaultAsync(s => s.Id == sid, ct) : null;
        var crumbs = new List<(string, string?)> { (c.T("nav.home"), "/"), (c.T("nav.blog"), "/posts"), (post.Title, $"/post/{id}") };
        var m = new StringBuilder("<article><h1>").Append(Html.Text(post.Title)).Append("</h1>");
        if (post.PublishedAt is { } at) m.Append("<p><time datetime=\"").Append(at.ToString("yyyy-MM-dd")).Append("\">").Append(at.ToString("yyyy-MM-dd")).Append("</time></p>");
        if (post.Tags.Count > 0) m.Append("<p>").Append(Html.Text(string.Join(" ", post.Tags.Select(t => "#" + t)))).Append("</p>");
        // ContentHtml is produced and sanitised by the server when the post is saved (see docs/ARCHITECTURE.md §9.6).
        m.Append("<div class=\"article\">").Append(post.ContentHtml).Append("</div>");
        if (linked is not null) m.Append("<p>").Append(Html.Text(c.T("post.watch"))).Append("<a href=\"").Append(SeriesPath(linked.Id)).Append("\">").Append(Html.Text(linked.Title)).Append("</a></p>");
        m.Append("</article>");
        var body = await FrameAsync(c, crumbs, m.ToString(), ct);
        var baseUrl = BaseFor(c);
        var cover = c.Signer.PublicUrlOrNull(post.CoverKey);
        var article = new Dictionary<string, object>
        {
            ["@context"] = "https://schema.org", ["@type"] = "Article", ["headline"] = Html.Clip(post.Title, 110), ["url"] = baseUrl + $"/post/{id}",
            ["dateModified"] = post.UpdatedAt.ToString("O"), ["publisher"] = new Dictionary<string, object> { ["@type"] = "Organization", ["name"] = c.Brand.Name },
        };
        if (post.PublishedAt is { } published) article["datePublished"] = published.ToString("O");
        if (cover is not null) article["image"] = PageTemplate.Absolute(cover, baseUrl);
        return new SeoPage(200, c.T("post.title", ("title", post.Title)), Html.Clip(string.IsNullOrWhiteSpace(post.Summary) ? post.Title : post.Summary, 150),
            $"/post/{id}", true, body, cover, "article", [article, Breadcrumbs(baseUrl, crumbs)]);
    }

    private static async Task<SeoPage> WayHome(Ctx c, CancellationToken ct)
    {
        var crumbs = new List<(string, string?)> { (c.T("nav.home"), "/"), (c.T("nav.wayHome"), "/go-home") };
        var m = new StringBuilder($"<h1>{Html.Text(c.T("way.heading"))}</h1><p>{Html.Text(c.T("way.lead"))}</p><h2>{Html.Text(c.T("way.lines"))}</h2><ul>");
        foreach (var mirror in c.App.Mirrors.Select(x => x.Trim().TrimEnd('/')).Where(x => x.Length > 0).Distinct())
            m.Append("<li><a href=\"").Append(Html.Attr(mirror)).Append("\">").Append(Html.Text(mirror)).Append("</a></li>");
        m.Append("</ul>");
        if (!string.IsNullOrWhiteSpace(c.Site.PermanentDomain)) m.Append("<p>").Append(Html.Text(c.T("way.permanent"))).Append(Html.Text(c.Site.PermanentDomain)).Append("</p>");
        if (!string.IsNullOrWhiteSpace(c.Site.Telegram)) m.Append("<p>").Append(Html.Text(c.T("way.group"))).Append("<a href=\"").Append(Html.Attr(c.Site.Telegram)).Append("\">").Append(Html.Text(c.Site.Telegram)).Append("</a></p>");
        if (!string.IsNullOrWhiteSpace(c.Site.Email)) m.Append("<p>").Append(Html.Text(c.T("way.email"))).Append(Html.Text(c.Site.Email)).Append("</p>");
        var body = await FrameAsync(c, crumbs, m.ToString(), ct);
        return new SeoPage(200, c.T("way.title"), c.T("way.description"), "/go-home", true, body);
    }

    private static async Task<SeoPage> Download(Ctx c, CancellationToken ct)
    {
        var releases = await c.Db.AppReleases.AsNoTracking().Where(r => r.Published).OrderByDescending(r => r.PublishedAt).Take(20).ToListAsync(ct);
        var crumbs = new List<(string, string?)> { (c.T("nav.home"), "/"), (c.T("footer.download"), "/app/home") };
        var m = new StringBuilder($"<h1>{Html.Text(c.T("download.heading"))}</h1><p>{Html.Text(c.T("download.lead"))}</p><ul>");
        foreach (var r in releases.GroupBy(r => r.Platform).Select(g => g.First()))
            m.Append("<li>").Append(Html.Text(r.Platform)).Append(" · ").Append(Html.Text(r.Version)).Append("</li>");
        m.Append("</ul>");
        var body = await FrameAsync(c, crumbs, m.ToString(), ct);
        return new SeoPage(200, c.T("download.title"), c.T("download.description"), "/app/home", true, body);
    }

    private static async Task<SeoPage> Legal(Ctx c, ReelGuo.Api.Legal.LegalPages.Page page, CancellationToken ct)
    {
        var title = page.Title(c.Brand);
        var notice = await ReelGuo.Api.Legal.LegalPages.UnderReviewAsync(c.Db, page.Slug, ct)
            ? $"<p class=\"legal-review\">{Html.Text(ReelGuo.Api.Legal.LegalPages.ReviewNotice(c.Brand))}</p>" : "";
        var text = ReelGuo.Api.Legal.LegalPages.Html(page, c.Renderer, c.Brand);
        var body = await FrameAsync(c, [(c.T("nav.home"), "/"), (title, page.Path)], $"<h1>{Html.Text(title)}</h1>{notice}<article>{text}</article>", ct);
        return new SeoPage(200, c.T("legal.title", ("page", title)), c.T("legal.description", ("page", title)), page.Path, true, body);
    }

    private static async Task<SeoPage> Private(Ctx c, CancellationToken ct) =>
        new(200, c.Brand.Name, c.T("private.description", ("tagline", c.Tagline)), null, false, await FrameAsync(c, [], "", ct));

    // notFound.body is our own text with a link in it (Sites/Texts), not visitor input.
    private static async Task<SeoPage> NotFound(Ctx c, CancellationToken ct) =>
        new(404, c.T("notFound.title"), c.T("notFound.description"), null, false,
            await FrameAsync(c, [], $"<h1>{Html.Text(c.T("notFound.heading"))}</h1><p>{c.T("notFound.body")}</p>", ct));

    /// <summary>Absolute URLs inside JSON-LD use the configured origin; without one they stay root-relative (resolved against the page).</summary>
    private static string BaseFor(Ctx c) => c.Seo.PublicBaseUrl.TrimEnd('/');

    // ---------------------------------------------------------------------------------------------
    // robots.txt and sitemaps
    // ---------------------------------------------------------------------------------------------

    private static IResult Robots(HttpContext http, Ctx c)
    {
        var text = !c.Seo.AllowIndexing
            ? "# Not for search engines (Seo:AllowIndexing is off).\nUser-agent: *\nDisallow: /\n"
            : $"User-agent: *\nAllow: /\nDisallow: /api/\nDisallow: /media/\nDisallow: /me\nDisallow: /store\nDisallow: /pay/\nDisallow: /settings\nDisallow: /search\n\nSitemap: {BaseUrl(c, http)}/sitemap.xml\n";
        return Results.Text(text, "text/plain; charset=utf-8");
    }

    private static async Task<IResult> SitemapIndex(HttpContext http, Ctx c, CancellationToken ct)
    {
        var baseUrl = BaseUrl(c, http);
        var count = await c.Visible(c.Seo.SitemapCountry).CountAsync(ct);
        var files = new List<string> { "pages.xml", "posts.xml" };
        for (var i = 0; i < Math.Max(1, (int)Math.Ceiling(count / (double)SitemapSeriesPerFile)); i++) files.Add($"series-{i + 1}.xml");
        return Xml(w =>
        {
            w.WriteStartElement("sitemapindex", Ns);
            foreach (var f in files)
            {
                w.WriteStartElement("sitemap", Ns);
                w.WriteElementString("loc", Ns, $"{baseUrl}/sitemaps/{f}");
                w.WriteEndElement();
            }
            w.WriteEndElement();
        });
    }

    private static async Task<IResult> SitemapFile(string name, HttpContext http, Ctx c, CancellationToken ct)
    {
        var baseUrl = BaseUrl(c, http);
        var urls = new List<(string Path, DateTimeOffset? Modified)>();
        if (name == "pages.xml")
        {
            urls.Add(("/", null));
            urls.Add(("/c/all", null));
            urls.Add(("/c/all?sort=original", null));
            foreach (var ch in await ChannelsAsync(c, ct)) if (ch.Status == CategoryStatus.Active) urls.Add(($"/c/{Html.Url(ch.Slug)}", null));
            foreach (var b in Boards) urls.Add(($"/ranks/{b.Kind}", null));
            urls.Add(("/topics", null));
            foreach (var t in await c.Db.Topics.AsNoTracking().Where(t => t.Published).Select(t => new { t.Id, t.UpdatedAt }).ToListAsync(ct)) urls.Add(($"/topics/{t.Id}", t.UpdatedAt));
            urls.Add(("/posts", null));
            urls.Add(("/go-home", null));
            urls.Add(("/app/home", null));
            foreach (var legal in LegalPages) urls.Add((legal.Path, null));
        }
        else if (name == "posts.xml")
        {
            foreach (var p in await PublishedPosts(c).OrderByDescending(p => p.PublishedAt).Take(40_000).Select(p => new { p.Id, p.UpdatedAt }).ToListAsync(ct))
                urls.Add(($"/post/{p.Id}", p.UpdatedAt));
        }
        else if (name.StartsWith("series-", StringComparison.Ordinal) && name.EndsWith(".xml", StringComparison.Ordinal)
            && int.TryParse(name["series-".Length..^".xml".Length], out var n) && n >= 1)
        {
            var rows = await c.Visible(c.Seo.SitemapCountry).OrderBy(s => s.Id).Skip((n - 1) * SitemapSeriesPerFile).Take(SitemapSeriesPerFile)
                .Select(s => new
                {
                    s.Id, Modified = s.LastEpisodeAt ?? s.UpdatedAt,
                    Episodes = s.Episodes.Where(e => e.Status == EpisodeStatus.Ready).OrderBy(e => e.EpisodeNumber).Select(e => e.EpisodeNumber).ToList(),
                }).ToListAsync(ct);
            if (rows.Count == 0 && n > 1) return Results.NotFound();
            foreach (var s in rows)
            {
                urls.Add((SeriesPath(s.Id), s.Modified));
                foreach (var e in s.Episodes.Where(e => e > 1)) urls.Add((SeriesPath(s.Id, e), s.Modified));
            }
        }
        else return Results.NotFound();

        return Xml(w =>
        {
            w.WriteStartElement("urlset", Ns);
            foreach (var (path, modified) in urls)
            {
                w.WriteStartElement("url", Ns);
                w.WriteElementString("loc", Ns, baseUrl + path);
                if (modified is { } m) w.WriteElementString("lastmod", Ns, m.UtcDateTime.ToString("yyyy-MM-dd"));
                w.WriteEndElement();
            }
            w.WriteEndElement();
        });
    }

    private const string Ns = "http://www.sitemaps.org/schemas/sitemap/0.9";

    private static IResult Xml(Action<XmlWriter> write)
    {
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8, OmitXmlDeclaration = true }))
            write(w);
        return Results.Text("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + sb, "application/xml; charset=utf-8");
    }
}
