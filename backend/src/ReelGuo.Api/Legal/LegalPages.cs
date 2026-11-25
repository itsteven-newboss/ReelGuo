using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ReelGuo.Api.Admin;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Data;
using ReelGuo.Data.Content;
using ReelGuo.Data.Entities;

namespace ReelGuo.Api.Legal;

/// <param name="UnderReview">Show the 待法务审核 notice above the text (on until an admin turns it off).</param>
public sealed record LegalPageDto(string Slug, string Path, string Title, string Html, bool UnderReview);

public sealed record AdminLegalPageDto(string Slug, string Path, string Title, string Html, bool UnderReview, DateTimeOffset? UpdatedAt, string? UpdatedBy);

public sealed record LegalReviewInput(bool UnderReview);

/// <summary>
/// The legal pages (用户协议, 隐私政策, 内容政策, 充值协议). Their text ships with each release, one copy per language
/// (Legal/Texts/&lt;locale&gt;/*.md, "{{site}}" becomes the site's name) and is shown in full; until an admin turns it off
/// in 法律条款, each page also shows that its text is still awaiting legal review. Read outside /api/v1, so blocked
/// regions can read them too.
/// </summary>
public static class LegalPages
{
    /// <param name="TitleKey">The page's name in Sites/Texts/&lt;locale&gt;.json.</param>
    public sealed record Page(string Slug, string Path, string TitleKey)
    {
        public string Title(SiteBrand brand) => brand.Text[TitleKey];
    }

    public static readonly IReadOnlyList<Page> All =
    [
        new("terms", "/terms", "legal.terms"),
        new("privacy", "/privacy", "legal.privacy"),
        new("content-policy", "/content-policy", "legal.contentPolicy"),
        new("recharge-terms", "/recharge-terms", "legal.rechargeTerms"),
    ];

    public static string ReviewNotice(SiteBrand brand) => brand.Text["legal.reviewNotice"];

    public static Page? BySlug(string slug) => All.FirstOrDefault(p => p.Slug == slug);
    public static Page? ByPath(string path) => All.FirstOrDefault(p => p.Path == path);

    private static readonly ConcurrentDictionary<string, string> Rendered = new();

    /// <summary>The page's text in the site's language as sanitised HTML (rendered once per process and site).</summary>
    public static string Html(Page page, PostRenderer renderer, SiteBrand brand) => Rendered.GetOrAdd($"{brand.Locale}|{brand.Name}|{page.Slug}", _ =>
    {
        using var stream = typeof(LegalPages).Assembly.GetManifestResourceStream($"legal/{brand.Locale}/{page.Slug}.md")
            ?? throw new InvalidOperationException($"Legal text legal/{brand.Locale}/{page.Slug}.md is missing from the build");
        using var reader = new StreamReader(stream);
        return renderer.Render(reader.ReadToEnd().Replace("{{site}}", brand.Name));
    });

    /// <summary>Whether the page still shows the review notice. Pages nobody has switched keep it on.</summary>
    public static async Task<bool> UnderReviewAsync(ReelGuoDbContext db, string slug, CancellationToken ct) =>
        await db.LegalReviews.AsNoTracking().Where(r => r.Slug == slug).Select(r => (bool?)r.UnderReview).FirstOrDefaultAsync(ct) ?? true;

    public static IEndpointRouteBuilder MapLegal(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/legal/{slug}", Get).WithTags("Legal")
            .WithSummary("A legal page's text and whether it is still awaiting legal review (not region-blocked).");
        return app;
    }

    public static RouteGroupBuilder MapAdminLegal(this RouteGroupBuilder admin)
    {
        var legal = admin.MapGroup("/legal").WithTags("Admin: legal");
        legal.MapGet("/", List).WithSummary("The legal pages with their text and review notice setting.");
        legal.MapPut("/{slug}", SetReview).WithSummary("Turns a page's 待法务审核 notice on or off.");
        return admin;
    }

    private static async Task<Results<Ok<LegalPageDto>, NotFound<ApiErrorResponse>>> Get(
        string slug, ReelGuoDbContext db, PostRenderer renderer, SiteBrand brand, CancellationToken ct)
    {
        var page = BySlug(slug);
        if (page is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "页面不存在"));
        return TypedResults.Ok(new LegalPageDto(page.Slug, page.Path, page.Title(brand), Html(page, renderer, brand), await UnderReviewAsync(db, slug, ct)));
    }

    private static async Task<Ok<List<AdminLegalPageDto>>> List(ReelGuoDbContext db, PostRenderer renderer, SiteBrand brand, CancellationToken ct)
    {
        var rows = await db.LegalReviews.AsNoTracking().ToDictionaryAsync(r => r.Slug, ct);
        return TypedResults.Ok(All.Select(p => rows.TryGetValue(p.Slug, out var r)
            ? new AdminLegalPageDto(p.Slug, p.Path, p.Title(brand), Html(p, renderer, brand), r.UnderReview, r.UpdatedAt, r.UpdatedBy)
            : new AdminLegalPageDto(p.Slug, p.Path, p.Title(brand), Html(p, renderer, brand), true, null, null)).ToList());
    }

    private static async Task<Results<Ok<AdminLegalPageDto>, NotFound<ApiErrorResponse>>> SetReview(
        string slug, LegalReviewInput input, HttpContext http, ReelGuoDbContext db, PostRenderer renderer, SiteBrand brand, TimeProvider time, CancellationToken ct)
    {
        var page = BySlug(slug);
        if (page is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "页面不存在"));
        var actor = AdminActor.From(http);
        var now = time.GetUtcNow();
        var row = await db.LegalReviews.FirstOrDefaultAsync(r => r.Slug == slug, ct);
        if (row is null) db.LegalReviews.Add(row = new LegalReview { Slug = slug, UpdatedBy = actor });
        row.UnderReview = input.UnderReview;
        row.UpdatedAt = now;
        row.UpdatedBy = actor;
        db.AddAudit(now, actor, "legal.review", "legal_page", slug, new { input.UnderReview });
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new AdminLegalPageDto(page.Slug, page.Path, page.Title(brand), Html(page, renderer, brand), row.UnderReview, row.UpdatedAt, row.UpdatedBy));
    }
}
