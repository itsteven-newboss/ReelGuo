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
using ReelGuo.Data.Entities;

namespace ReelGuo.Api.Seo;

/// <summary>HTML and JSON escaping for server-rendered pages.</summary>
public static class Html
{
    public static string Text(string? s) => WebUtility.HtmlEncode(s ?? "");
    public static string Attr(string? s) => WebUtility.HtmlEncode(s ?? "");
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

    private static readonly (string Kind, RankKind Rank, string Name)[] Boards =
        [("hot", RankKind.Hot, "热播榜"), ("recommend", RankKind.Recommend, "推荐榜"), ("potential", RankKind.Potential, "飙升榜")];

    private static readonly Dictionary<string, string> LegalPages = new()
    {
        ["/terms"] = "用户协议", ["/privacy"] = "隐私政策", ["/content-policy"] = "内容政策", ["/recharge-terms"] = "充值协议",
    };

    private static readonly string[] PrivatePages = ["/search", "/me", "/me/wallet", "/store", "/pay/result", "/settings"];

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
        pages.MapMethods("/download", methods, (HttpContext h, Ctx c, CancellationToken ct) => Serve(h, c, ct, (_, ct2) => Download(c, ct2)));
        foreach (var (path, title) in LegalPages)
            pages.MapMethods(path, methods, (HttpContext h, Ctx c, CancellationToken ct) => Serve(h, c, ct, (_, ct2) => Legal(c, path, title, ct2), legal: true));
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
        IOptions<AppOptions> app, ICountryResolver countries, IBlockedCountryStore blocked, IAgeRuleStore ages, IMemoryCache cache, TimeProvider time)
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
        if (!legal && (await c.Blocked.GetAsync(ct)).Contains(country))
            page = new SeoPage(StatusCodes.Status451UnavailableForLegalReasons, "ReelGuo", "当前地区暂不提供服务", null, false, "");
        else if (!legal && await c.Ages.ModeForAsync(country, ct) == AgeCheckMode.Strict)
            page = new SeoPage(StatusCodes.Status200OK, "ReelGuo", "当前地区需完成年龄验证后才能观看", null, false, "");
        else
        {
            var key = $"seo|{country}|{http.Request.Path}{http.Request.QueryString}";
            page = (await c.Cache.GetOrCreateAsync(key, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(Math.Max(5, c.Seo.CacheSeconds));
                return await build(country, ct);
            }))!;
        }

        var html = PageTemplate.Render(template, page, baseUrl, c.Seo.AllowIndexing);
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
        b.Append("<header><a href=\"/\"><strong>ReelGuo</strong></a> <nav aria-label=\"主导航\"><a href=\"/\">首页</a>");
        b.Append(" <a href=\"/c/all?sort=original\">ReelGuo原创</a>");
        foreach (var ch in channels.Where(x => x.Status == CategoryStatus.Active))
            b.Append(" <a href=\"/c/").Append(Html.Url(ch.Slug)).Append("\">").Append(Html.Text(ch.Name)).Append("</a>");
        b.Append(" <a href=\"/topics\">专题</a> <a href=\"/ranks\">排行榜</a> <a href=\"/posts\">博客</a> <a href=\"/go-home\">回家的路</a> <a href=\"/download\">ReelGuo APP</a></nav></header>\n");
        b.Append("<noscript><p><strong>本站含有成人内容，仅限年满 18 周岁的成年人访问。</strong></p></noscript>\n");
        var list = crumbs.ToList();
        if (list.Count > 1)
        {
            b.Append("<nav aria-label=\"位置\" class=\"crumbs\">");
            for (var i = 0; i < list.Count; i++)
            {
                if (i > 0) b.Append(" / ");
                var (label, href) = list[i];
                b.Append(href is null ? Html.Text(label) : $"<a href=\"{Html.Attr(href)}\">{Html.Text(label)}</a>");
            }
            b.Append("</nav>\n");
        }
        b.Append("<main>").Append(main).Append("</main>\n");
        b.Append("<footer><nav aria-label=\"页脚\"><a href=\"/go-home\">回家的路</a> <a href=\"/download\">下载 App</a> <a href=\"/c/all\">最近上新</a> <a href=\"/ranks\">热门推荐</a> <a href=\"/topics\">专题合集</a>");
        foreach (var (path, title) in LegalPages) b.Append(" <a href=\"").Append(path).Append("\">").Append(title).Append("</a>");
        b.Append("</nav><p>© ReelGuo · 本站内容仅面向年满 18 周岁的成年人</p></footer>");
        return b.ToString();
    }

    private static string SeriesPath(long id, int? ep = null) => ep is > 1 ? $"/video/{id}/ep-{ep}" : $"/video/{id}";

    private static string ProgressLabel(Series s) => s.IsFinished ? $"全{Math.Max(s.EpisodeCount, s.TotalEpisodes)}集" : $"更新至{s.EpisodeCount}集";

    /// <summary>A list of series: title link, channel, progress, tags, synopsis.</summary>
    private static string SeriesList(IEnumerable<Series> items, bool ordered = false)
    {
        var b = new StringBuilder(ordered ? "<ol class=\"cards\">" : "<ul class=\"cards\">");
        foreach (var s in items)
        {
            b.Append("<li><a href=\"").Append(SeriesPath(s.Id)).Append("\">").Append(Html.Text(s.Title)).Append("</a>");
            b.Append(" <small>").Append(Html.Text(s.Category?.Name ?? "")).Append(" · ").Append(ProgressLabel(s));
            if (s.Score > 0) b.Append(" · ").Append(s.Score.ToString("0.0")).Append("分");
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

    private const string SiteName = "ReelGuo";

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
        m.Append("<h1>ReelGuo · ").Append(Html.Text(c.Site.Tagline)).Append("</h1>");
        m.Append("<p>ReelGuo 每日更新 AI 短剧、原创短剧与 AI 漫剧，支持手机与电脑在线观看。仅限年满 18 周岁的成年人访问。</p>");
        m.Append("<section><h2>精选推荐</h2>").Append(SeriesList(picks)).Append("</section>");
        m.Append("<section><h2>最近上新</h2>").Append(SeriesList(latest)).Append("<p><a href=\"/c/all\">全部上新</a></p></section>");
        m.Append("<section><h2>热播短剧</h2>").Append(SeriesList(hot)).Append("<p><a href=\"/ranks/hot\">更多热播</a></p></section>");
        if (topics.Count > 0)
        {
            m.Append("<section><h2>专题推荐</h2><ul>");
            foreach (var t in topics) m.Append("<li><a href=\"/topics/").Append(t.Id).Append("\">").Append(Html.Text(t.Title)).Append("</a> <small>").Append(Html.Text(Html.Clip(t.Description, 80))).Append("</small></li>");
            m.Append("</ul></section>");
        }
        var body = await FrameAsync(c, [], m.ToString(), ct);
        var site = new Dictionary<string, object>
        {
            ["@context"] = "https://schema.org", ["@type"] = "WebSite", ["name"] = SiteName, ["url"] = BaseFor(c) + "/",
            ["potentialAction"] = new Dictionary<string, object>
            {
                ["@type"] = "SearchAction", ["target"] = BaseFor(c) + "/search?q={search_term_string}", ["query-input"] = "required name=search_term_string",
            },
        };
        return new SeoPage(200, $"ReelGuo · {c.Site.Tagline}",
            $"ReelGuo：{c.Site.Tagline}。每日更新 AI 短剧、原创短剧与 AI 漫剧，支持手机与电脑在线观看，仅限 18 岁以上成年人访问。", "/", true, body,
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
        var name = original ? "ReelGuo原创" : all ? "全部短剧" : channel!.Name;
        var intro = channel?.Description is { Length: > 0 } d ? d : "精选热播短剧，汇集独家原创、精选推荐与最新更新内容。";
        var canonical = $"/c/{Html.Url(slug)}" + (original ? "?sort=original" : "") + (page > 1 ? (original ? "&" : "?") + $"page={page}" : "");
        var crumbs = new List<(string, string?)> { ("首页", "/"), (name, canonical) };

        if (channel?.Status == CategoryStatus.ComingSoon)
        {
            var soon = await FrameAsync(c, crumbs, $"<h1>{Html.Text(name)}</h1><p>该频道即将上线，敬请期待。</p>", ct);
            return new SeoPage(200, $"{name} - {SiteName}", intro, canonical, false, soon);
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
        m.Append("<h1>").Append(Html.Text(name)).Append("在线观看</h1><p>【").Append(Html.Text(name)).Append("】").Append(Html.Text(intro)).Append($" 共 {total} 部。</p>");
        m.Append(SeriesList(items));
        var basePath = $"/c/{Html.Url(slug)}" + (original ? "?sort=original" : "");
        var join = original ? "&" : "?";
        m.Append("<nav aria-label=\"分页\">");
        if (page > 1) m.Append("<a rel=\"prev\" href=\"").Append(page == 2 ? basePath : $"{basePath}{join}page={page - 1}").Append("\">上一页</a> ");
        m.Append($"第 {page} / {pages} 页");
        if (page < pages) m.Append(" <a rel=\"next\" href=\"").Append($"{basePath}{join}page={page + 1}").Append("\">下一页</a>");
        m.Append("</nav>");
        var body = await FrameAsync(c, crumbs, m.ToString(), ct);
        var title = $"{name}在线观看{(page > 1 ? $" - 第{page}页" : "")} - {SiteName}";
        return new SeoPage(200, title, $"【{name}】{intro}", canonical, true, body, JsonLd: [Breadcrumbs(BaseFor(c), crumbs)]);
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

        var episodeTitle = current is null || current.EpisodeNumber == 1 && ep is null ? null : $"第{current.EpisodeNumber}集";
        var canonical = SeriesPath(s.Id, current?.EpisodeNumber);
        var crumbs = new List<(string, string?)> { ("首页", "/"), (s.Category.Name, $"/c/{Html.Url(s.Category.Slug)}"), (s.Title, SeriesPath(s.Id)) };
        if (episodeTitle is not null) crumbs.Add((episodeTitle, canonical));

        var m = new StringBuilder();
        m.Append("<article><h1>").Append(Html.Text(s.Title)).Append(episodeTitle is null ? "" : $" {episodeTitle}").Append("</h1>");
        if (current is not null && !string.IsNullOrWhiteSpace(current.Title) && current.Title != episodeTitle)
            m.Append("<h2>").Append(Html.Text(current.Title)).Append("</h2>");
        m.Append("<p>").Append(Html.Text(string.IsNullOrWhiteSpace(s.Description) ? "暂无简介" : s.Description)).Append("</p>");
        m.Append("<dl><dt>频道</dt><dd><a href=\"/c/").Append(Html.Url(s.Category.Slug)).Append("\">").Append(Html.Text(s.Category.Name)).Append("</a></dd>");
        m.Append("<dt>更新状态</dt><dd>").Append(ProgressLabel(s)).Append("</dd>");
        if (s.PublishedAt is { } at) m.Append("<dt>上线日期</dt><dd>").Append(at.ToString("yyyy-MM-dd")).Append("</dd>");
        if (s.Score > 0) m.Append("<dt>评分</dt><dd>").Append(s.Score.ToString("0.0")).Append("分</dd>");
        if (s.Author is not null) m.Append("<dt>出品</dt><dd>").Append(Html.Text(s.Author.Nickname)).Append("</dd>");
        if (s.Tags.Count > 0)
        {
            m.Append("<dt>标签</dt><dd>");
            foreach (var t in s.Tags) m.Append("<a href=\"/search?q=").Append(Html.Url(t)).Append("\">").Append(Html.Text(t)).Append("</a> ");
            m.Append("</dd>");
        }
        m.Append("</dl>");
        if (episodes.Count > 0)
        {
            m.Append("<section><h2>选集</h2><ol class=\"episodes\">");
            foreach (var e in episodes)
                m.Append("<li><a href=\"").Append(e.EpisodeNumber == 1 ? SeriesPath(s.Id) : SeriesPath(s.Id, e.EpisodeNumber)).Append("\">第")
                    .Append(e.EpisodeNumber).Append("集</a></li>");
            m.Append("</ol></section>");
        }
        m.Append("</article>");
        if (related.Count > 0) m.Append("<section><h2>猜你喜欢</h2>").Append(SeriesList(related)).Append("</section>");
        var body = await FrameAsync(c, crumbs, m.ToString(), ct);

        var baseUrl = BaseFor(c);
        var cover = c.Signer.PublicUrlOrNull(s.CoverKey);
        var show = new Dictionary<string, object>
        {
            ["@context"] = "https://schema.org", ["@type"] = "TVSeries", ["name"] = s.Title, ["url"] = baseUrl + SeriesPath(s.Id),
            ["description"] = Html.Clip(s.Description, 300), ["genre"] = new[] { s.Category.Name }.Concat(s.Tags).ToList(),
            ["numberOfEpisodes"] = Math.Max(s.TotalEpisodes, s.EpisodeCount), ["inLanguage"] = "zh-CN", ["isFamilyFriendly"] = false,
            ["contentRating"] = "18+",
            ["episode"] = episodes.Take(100).Select(e => new Dictionary<string, object>
            {
                ["@type"] = "TVEpisode", ["episodeNumber"] = e.EpisodeNumber, ["name"] = $"第{e.EpisodeNumber}集",
                ["url"] = baseUrl + (e.EpisodeNumber == 1 ? SeriesPath(s.Id) : SeriesPath(s.Id, e.EpisodeNumber)),
            }).ToList(),
        };
        if (cover is not null) show["image"] = PageTemplate.Absolute(cover, baseUrl);
        if (s.PublishedAt is { } published) show["datePublished"] = published.ToString("yyyy-MM-dd");
        if (rating.Count >= 5 && rating.Average is { } avg)
            show["aggregateRating"] = new Dictionary<string, object> { ["@type"] = "AggregateRating", ["ratingValue"] = Math.Round(avg, 1), ["bestRating"] = 10, ["worstRating"] = 1, ["ratingCount"] = rating.Count };

        var title = episodeTitle is null
            ? $"{s.Title}全集在线观看 - {s.Category.Name} - {SiteName}"
            : $"{s.Title} {episodeTitle}在线观看 - {s.Category.Name} - {SiteName}";
        var description = $"{Html.Clip(s.Description, 130)} {ProgressLabel(s)}，{s.Category.Name}在线观看。".Trim();
        return new SeoPage(200, title, description, canonical, true, body, cover, episodeTitle is null ? "video.tv_show" : "video.episode",
            [show, Breadcrumbs(baseUrl, crumbs)]);
    }

    private static async Task<SeoPage> Ranks(Ctx c, string kind, string country, CancellationToken ct)
    {
        var board = Boards.FirstOrDefault(b => b.Kind == kind);
        if (board.Kind is null) return await NotFound(c, ct);
        var visible = c.Visible(country);
        var rows = await c.Db.Ranks.AsNoTracking().Where(r => r.Kind == board.Rank && visible.Any(s => s.Id == r.SeriesId))
            .OrderBy(r => r.Position).Take(20).Select(r => r.Series).Include(s => s.Category).ToListAsync(ct);
        var crumbs = new List<(string, string?)> { ("首页", "/"), ("排行榜", "/ranks"), (board.Name, $"/ranks/{board.Kind}") };
        var m = new StringBuilder($"<h1>{board.Name}</h1><p>ReelGuo 榜单 · TOP20 · 每 10 分钟更新。</p>");
        m.Append("<nav>");
        foreach (var b in Boards) m.Append($"<a href=\"/ranks/{b.Kind}\">{b.Name}</a> ");
        m.Append("</nav>").Append(SeriesList(rows, ordered: true));
        var body = await FrameAsync(c, crumbs, m.ToString(), ct);
        return new SeoPage(200, $"{board.Name} TOP20 - ReelGuo排行榜", $"ReelGuo{board.Name}：全站最受欢迎的 20 部短剧，每 10 分钟更新。",
            $"/ranks/{board.Kind}", true, body, JsonLd: [Breadcrumbs(BaseFor(c), crumbs)]);
    }

    private static async Task<SeoPage> Topics(Ctx c, string country, CancellationToken ct)
    {
        var topics = await c.Db.Topics.AsNoTracking().Where(t => t.Published).OrderBy(t => t.SortOrder).ThenByDescending(t => t.Id).Take(200).ToListAsync(ct);
        var crumbs = new List<(string, string?)> { ("首页", "/"), ("专题", "/topics") };
        var m = new StringBuilder("<h1>全部专题</h1><p>专题集中展示 ReelGuo 各类精品专题合集，按主题整理，方便快速找到想看的短剧。</p><ul>");
        foreach (var t in topics)
            m.Append("<li><a href=\"/topics/").Append(t.Id).Append("\">").Append(Html.Text(t.Title)).Append("</a> <small>").Append(Html.Text(Html.Clip(t.Description, 100))).Append("</small></li>");
        m.Append("</ul>");
        var body = await FrameAsync(c, crumbs, m.ToString(), ct);
        return new SeoPage(200, $"全部专题 - {SiteName}", "ReelGuo 专题合集：按题材与看点整理的短剧合集，持续更新。", "/topics", true, body);
    }

    private static async Task<SeoPage> Topic(Ctx c, int id, string country, CancellationToken ct)
    {
        var topic = await c.Db.Topics.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && t.Published, ct);
        if (topic is null) return await NotFound(c, ct);
        var order = await c.Db.TopicSeries.AsNoTracking().Where(x => x.TopicId == id).OrderBy(x => x.Position).Select(x => x.SeriesId).ToListAsync(ct);
        var series = await c.Visible(country).Include(s => s.Category).Where(s => order.Contains(s.Id)).ToListAsync(ct);
        var items = series.OrderBy(s => order.IndexOf(s.Id)).ToList();
        var crumbs = new List<(string, string?)> { ("首页", "/"), ("专题", "/topics"), (topic.Title, $"/topics/{id}") };
        var m = $"<h1>{Html.Text(topic.Title)}</h1><p>{Html.Text(topic.Description)} 共 {items.Count} 部。</p>{SeriesList(items)}";
        var body = await FrameAsync(c, crumbs, m, ct);
        return new SeoPage(200, $"{topic.Title} - ReelGuo专题", string.IsNullOrWhiteSpace(topic.Description) ? $"{topic.Title}：ReelGuo 短剧专题合集。" : Html.Clip(topic.Description, 150),
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
        var crumbs = new List<(string, string?)> { ("首页", "/"), ("博客", "/posts") };
        var m = new StringBuilder("<h1>博客</h1><p>浏览最新博客文章，发现圈内热点与短剧剧评。</p><ul>");
        foreach (var x in posts)
        {
            m.Append("<li><a href=\"/post/").Append(x.Id).Append("\">").Append(Html.Text(x.Title)).Append("</a>");
            if (x.PublishedAt is { } at) m.Append(" <small>").Append(at.ToString("yyyy-MM-dd")).Append("</small>");
            if (!string.IsNullOrWhiteSpace(x.Summary)) m.Append("<p>").Append(Html.Text(Html.Clip(x.Summary, 120))).Append("</p>");
            m.Append("</li>");
        }
        m.Append("</ul><nav aria-label=\"分页\">");
        if (page > 1) m.Append("<a rel=\"prev\" href=\"").Append(page == 2 ? "/posts" : $"/posts?page={page - 1}").Append("\">上一页</a> ");
        m.Append($"第 {page} / {pages} 页");
        if (page < pages) m.Append($" <a rel=\"next\" href=\"/posts?page={page + 1}\">下一页</a>");
        m.Append("</nav>");
        var body = await FrameAsync(c, crumbs, m.ToString(), ct);
        return new SeoPage(200, $"博客{(page > 1 ? $" - 第{page}页" : "")} - {SiteName}", "ReelGuo 博客：圈内热点、短剧剧评与幕后内容，每天更新。",
            page > 1 ? $"/posts?page={page}" : "/posts", true, body);
    }

    private static async Task<SeoPage> Post(Ctx c, long id, string country, CancellationToken ct)
    {
        var post = await PublishedPosts(c).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (post is null) return await NotFound(c, ct);
        Series? linked = post.SeriesId is { } sid ? await c.Visible(country).FirstOrDefaultAsync(s => s.Id == sid, ct) : null;
        var crumbs = new List<(string, string?)> { ("首页", "/"), ("博客", "/posts"), (post.Title, $"/post/{id}") };
        var m = new StringBuilder("<article><h1>").Append(Html.Text(post.Title)).Append("</h1>");
        if (post.PublishedAt is { } at) m.Append("<p><time datetime=\"").Append(at.ToString("yyyy-MM-dd")).Append("\">").Append(at.ToString("yyyy-MM-dd")).Append("</time></p>");
        if (post.Tags.Count > 0) m.Append("<p>").Append(Html.Text(string.Join(" ", post.Tags.Select(t => "#" + t)))).Append("</p>");
        // ContentHtml is produced and sanitised by the server when the post is saved (see docs/ARCHITECTURE.md §9.6).
        m.Append("<div class=\"article\">").Append(post.ContentHtml).Append("</div>");
        if (linked is not null) m.Append("<p>观看正片：<a href=\"").Append(SeriesPath(linked.Id)).Append("\">").Append(Html.Text(linked.Title)).Append("</a></p>");
        m.Append("</article>");
        var body = await FrameAsync(c, crumbs, m.ToString(), ct);
        var baseUrl = BaseFor(c);
        var cover = c.Signer.PublicUrlOrNull(post.CoverKey);
        var article = new Dictionary<string, object>
        {
            ["@context"] = "https://schema.org", ["@type"] = "Article", ["headline"] = Html.Clip(post.Title, 110), ["url"] = baseUrl + $"/post/{id}",
            ["dateModified"] = post.UpdatedAt.ToString("O"), ["publisher"] = new Dictionary<string, object> { ["@type"] = "Organization", ["name"] = SiteName },
        };
        if (post.PublishedAt is { } published) article["datePublished"] = published.ToString("O");
        if (cover is not null) article["image"] = PageTemplate.Absolute(cover, baseUrl);
        return new SeoPage(200, $"{post.Title} - ReelGuo博客", Html.Clip(string.IsNullOrWhiteSpace(post.Summary) ? post.Title : post.Summary, 150),
            $"/post/{id}", true, body, cover, "article", [article, Breadcrumbs(baseUrl, crumbs)]);
    }

    private static async Task<SeoPage> WayHome(Ctx c, CancellationToken ct)
    {
        var crumbs = new List<(string, string?)> { ("首页", "/"), ("回家的路", "/go-home") };
        var m = new StringBuilder("<h1>ReelGuo · 官方最新地址发布页</h1><p>本页面由 ReelGuo 官方维护，实时同步最新可用线路与备用入口，方便在不同网络环境下快速返回。</p><h2>最新可用地址</h2><ul>");
        foreach (var mirror in c.App.Mirrors.Select(x => x.Trim().TrimEnd('/')).Where(x => x.Length > 0).Distinct())
            m.Append("<li><a href=\"").Append(Html.Attr(mirror)).Append("\">").Append(Html.Text(mirror)).Append("</a></li>");
        m.Append("</ul>");
        if (!string.IsNullOrWhiteSpace(c.Site.PermanentDomain)) m.Append("<p>官方永久域名：").Append(Html.Text(c.Site.PermanentDomain)).Append("</p>");
        if (!string.IsNullOrWhiteSpace(c.Site.Telegram)) m.Append("<p>官方交流群：<a href=\"").Append(Html.Attr(c.Site.Telegram)).Append("\">").Append(Html.Text(c.Site.Telegram)).Append("</a></p>");
        if (!string.IsNullOrWhiteSpace(c.Site.Email)) m.Append("<p>官方邮箱：").Append(Html.Text(c.Site.Email)).Append("</p>");
        var body = await FrameAsync(c, crumbs, m.ToString(), ct);
        return new SeoPage(200, "回家的路 · ReelGuo 官方最新地址发布页", "ReelGuo 官方最新地址发布页：最新可用线路、备用入口与官方联系方式。", "/go-home", true, body);
    }

    private static async Task<SeoPage> Download(Ctx c, CancellationToken ct)
    {
        var releases = await c.Db.AppReleases.AsNoTracking().Where(r => r.Published).OrderByDescending(r => r.PublishedAt).Take(20).ToListAsync(ct);
        var crumbs = new List<(string, string?)> { ("首页", "/"), ("下载 App", "/download") };
        var m = new StringBuilder("<h1>下载 ReelGuo App</h1><p>桌面版与安卓版 App，自动切换可用线路。iPhone 可将网页添加到主屏幕使用。</p><ul>");
        foreach (var r in releases.GroupBy(r => r.Platform).Select(g => g.First()))
            m.Append("<li>").Append(Html.Text(r.Platform)).Append(" · ").Append(Html.Text(r.Version)).Append("</li>");
        m.Append("</ul>");
        var body = await FrameAsync(c, crumbs, m.ToString(), ct);
        return new SeoPage(200, $"下载 App - {SiteName}", "下载 ReelGuo 桌面版与安卓版 App。", "/download", true, body);
    }

    private static async Task<SeoPage> Legal(Ctx c, string path, string title, CancellationToken ct)
    {
        var body = await FrameAsync(c, [("首页", "/"), (title, path)], $"<h1>{title}</h1>", ct);
        return new SeoPage(200, $"{title} - {SiteName}", $"ReelGuo {title}。", path, true, body);
    }

    private static async Task<SeoPage> Private(Ctx c, CancellationToken ct) =>
        new(200, SiteName, $"ReelGuo · {c.Site.Tagline}", null, false, await FrameAsync(c, [], "", ct));

    private static async Task<SeoPage> NotFound(Ctx c, CancellationToken ct) =>
        new(404, $"页面不存在 - {SiteName}", "页面不存在或已下架。", null, false,
            await FrameAsync(c, [], "<h1>页面不存在</h1><p>链接可能已失效，<a href=\"/\">回到首页</a>看看吧。</p>", ct));

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
            urls.Add(("/download", null));
            foreach (var path in LegalPages.Keys) urls.Add((path, null));
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
