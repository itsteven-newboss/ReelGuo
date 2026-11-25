using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReelGuo.Api.Apps;
using ReelGuo.Api.Legal;
using ReelGuo.Data;
using ReelGuo.Data.Content;
using static ReelGuo.Api.Tests.HttpTestExtensions;

namespace ReelGuo.Api.Tests;

/// <summary>The server's text in every site language: the same keys and placeholders, and legal pages for each.</summary>
public sealed class SiteTextTests
{
    public static TheoryData<string> OtherLocales() => [.. SiteText.Locales.Where(l => l != "zh-CN")];

    [Theory]
    [MemberData(nameof(OtherLocales))]
    public void Every_language_has_the_chinese_keys_and_placeholders(string locale)
    {
        var zh = SiteText.For("zh-CN");
        var other = SiteText.For(locale);
        Assert.Equal(zh.Keys.Order(), other.Keys.Order());
        // A translation may leave out the site's name ({brand}, {short}); every other value must stay, and none be added.
        string[] optional = ["brand", "short"];
        foreach (var key in zh.Keys.Where(k => !k.Contains("fallbackTags")))
        {
            var mine = other.Placeholders(key).ToHashSet();
            var source = zh.Placeholders(key).ToHashSet();
            Assert.True(mine.IsSubsetOf(source) && source.Except(optional).All(mine.Contains),
                $"{locale} \"{key}\": {{{string.Join("}, {", mine)}}} for {{{string.Join("}, {", source)}}}");
        }
        Assert.Equal(24, other.List("search.fallbackTags").Count);
    }

    [Theory]
    [MemberData(nameof(OtherLocales))]
    public void Every_language_has_every_legal_page(string locale)
    {
        var brand = new SiteBrand(new BrandOptions { Key = "test", Name = "Testdrama", Short = "Testdrama", Locale = locale });
        var renderer = new PostRenderer("/media");
        foreach (var page in LegalPages.All)
        {
            var html = LegalPages.Html(page, renderer, brand);
            Assert.Contains("<h2>", html);
            Assert.Contains("Testdrama", html);
            Assert.DoesNotContain("{{site}}", html);
            Assert.DoesNotContain("黄月", html);
            Assert.True(html.Length > 800, $"{locale}/{page.Slug}: only {html.Length} characters");
        }
    }

    [Fact]
    public void Text_fills_in_the_brand_and_values()
    {
        var brand = new SiteBrand(new BrandOptions { Key = "yelrama", Name = "Yelrama", Short = "Yelrama", Locale = "id" });
        Assert.Equal("Membuka «Ratu» episode 3", brand.T("wallet.unlock", ("title", "Ratu"), ("ep", 3)));
        Assert.Equal("© Yelrama · Konten situs ini khusus untuk usia 18 tahun ke atas", brand.T("footer.rights"));
        Assert.Throws<InvalidOperationException>(() => new SiteBrand(new BrandOptions { Locale = "fr" }));
    }
}

/// <summary>An API serving another site (Yelrama, Indonesian): its name, language, default content and iPhone profile.</summary>
public sealed class SiteTests(PostgresFixture db) : IClassFixture<PostgresFixture>, IDisposable
{
    private readonly string _templatePath = Path.Combine(Path.GetTempPath(), $"reelguo-site-{Guid.NewGuid():N}.html");

    public void Dispose()
    {
        if (File.Exists(_templatePath)) File.Delete(_templatePath);
    }

    private ApiFactory Api()
    {
        File.WriteAllText(_templatePath, """
            <!doctype html>
            <html lang="id">
              <head><meta charset="UTF-8" /><meta name="robots" content="noindex" /><title>Yelrama</title></head>
              <body><div id="root"></div></body>
            </html>
            """);
        return db.CreateApi(new Dictionary<string, string?>
        {
            ["Brand:Key"] = "yelrama",
            ["Brand:Name"] = "Yelrama",
            ["Brand:Short"] = "Yelrama",
            ["Brand:Locale"] = "id",
            ["Seo:TemplatePath"] = _templatePath,
            ["Seo:PublicBaseUrl"] = "https://yelrama.example",
            ["Seo:CacheSeconds"] = "5",
        });
    }

    private static Task<HttpResponseMessage> Get(HttpClient client, string url) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Get, url).From("ID"), Ct);

    [Fact]
    public async Task The_site_speaks_its_language_and_its_default_content_is_translated_once()
    {
        await using var api = Api();
        var client = api.CreateClient();

        var site = await Get(client, "/api/v1/site").OkJsonAsync();
        Assert.Equal("Yelrama", site.GetProperty("name").GetString());
        Assert.Equal("Drama pendek AI dan orisinal, tonton online", site.GetProperty("tagline").GetString());

        // The seeded channels are renamed; the face-swap placeholder is hidden.
        var categories = (await Get(client, "/api/v1/categories").OkJsonAsync()).EnumerateArray()
            .ToDictionary(c => c.GetProperty("slug").GetString()!, c => c.GetProperty("name").GetString());
        Assert.Equal("Drama AI", categories["ai-duanju"]);
        Assert.False(categories.ContainsKey("ai-huanlian"));

        await using (var scope = api.Services.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ReelGuoDbContext>();
            var products = await ctx.Products.AsNoTracking().OrderBy(p => p.Id).ToListAsync(Ct);
            Assert.Equal("100 koin", products[0].Name);
            Assert.Equal(("500 koin", "Populer"), (products[1].Name, products[1].Badge));
            Assert.Equal("VIP bulanan", products[5].Name);

            // An admin's own wording is never overwritten, not even on the next start.
            var channel = await ctx.Categories.FirstAsync(c => c.Slug == "ai-manju", Ct);
            channel.Name = "Komik Saya";
            await ctx.SaveChangesAsync(Ct);
        }
        await using (var again = Api())
        {
            var names = (await Get(again.CreateClient(), "/api/v1/categories").OkJsonAsync()).EnumerateArray()
                .Select(c => c.GetProperty("name").GetString()).ToList();
            Assert.Contains("Komik Saya", names);
        }

        var suggest = await Get(client, "/api/v1/search/suggest?seed=1").OkJsonAsync();
        Assert.Contains("Romansa", suggest.GetProperty("hot_tags").EnumerateArray().Select(t => t.GetString()));

        var terms = await Get(client, "/api/legal/terms").OkJsonAsync();
        Assert.Equal("Ketentuan Layanan", terms.GetProperty("title").GetString());
        Assert.Contains("Yelrama", terms.GetProperty("html").GetString());
    }

    [Fact]
    public async Task Search_engine_pages_and_the_iphone_profile_carry_the_site_name()
    {
        await using var api = Api();
        var client = api.CreateClient();

        var home = await (await Get(client, "/")).Content.ReadAsStringAsync(Ct);
        Assert.Contains("<meta property=\"og:site_name\" content=\"Yelrama\" />", home);
        Assert.Contains(">Beranda</a>", home);
        Assert.Contains("Situs ini berisi konten dewasa", home);
        Assert.DoesNotContain("首页", home);
        Assert.DoesNotContain("黄月", home);

        var notFound = await Get(client, "/no-such-page");
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Contains("<title>Halaman tidak ditemukan - Yelrama</title>", await notFound.Content.ReadAsStringAsync(Ct));

        var request = new HttpRequestMessage(HttpMethod.Get, "http://m.yelrama.example/app/download/ios");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1");
        var profile = await client.SendAsync(request.From("ID"), Ct);
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        var plist = Encoding.UTF8.GetString(await profile.Content.ReadAsByteArrayAsync(Ct));
        Assert.Contains("<string>Yelrama</string>", plist);
        Assert.Contains("<string>com.reelguo.webclip.yelrama</string>", plist);
        Assert.Contains("Tambahkan «Yelrama» ke layar utama", plist);
        Assert.Equal(WebClipProfile.IconFor(new SiteBrand(new BrandOptions { Key = "yelrama", Locale = "id" })),
            Convert.FromBase64String(plist.Split("<data>")[1].Split("</data>")[0]));
    }
}
