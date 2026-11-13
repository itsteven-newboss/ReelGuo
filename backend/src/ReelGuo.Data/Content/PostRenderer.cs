using Ganss.Xss;
using Markdig;

namespace ReelGuo.Data.Content;

/// <summary>
/// Turns admin-written Markdown into the HTML the app displays. Raw HTML in the Markdown is not
/// interpreted, and the output is sanitised again: only basic formatting, http(s) links (opened in a
/// new tab, no referrer) and images hosted on our own media host survive.
/// The app sanitises once more on display (DOMPurify), so a bug in one layer is not enough.
/// </summary>
public sealed class PostRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    private readonly HtmlSanitizer _sanitizer;
    private readonly string _mediaBase;

    /// <param name="mediaPublicBaseUrl">Media:PublicBaseUrl, e.g. "/media" or "https://media.example.com".</param>
    public PostRenderer(string mediaPublicBaseUrl)
    {
        _mediaBase = mediaPublicBaseUrl.TrimEnd('/') + "/p/";
        _sanitizer = new HtmlSanitizer();
        _sanitizer.AllowedTags.Clear();
        foreach (var tag in new[] { "p", "br", "strong", "em", "del", "h2", "h3", "h4", "ul", "ol", "li", "blockquote", "a", "img", "hr", "code", "pre" })
            _sanitizer.AllowedTags.Add(tag);
        _sanitizer.AllowedAttributes.Clear();
        foreach (var attr in new[] { "href", "src", "alt", "title" })
            _sanitizer.AllowedAttributes.Add(attr);
        _sanitizer.AllowedSchemes.Clear();
        _sanitizer.AllowedSchemes.Add("http");
        _sanitizer.AllowedSchemes.Add("https");
        _sanitizer.AllowedCssProperties.Clear();
        _sanitizer.AllowDataAttributes = false;

        _sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is not AngleSharp.Dom.IElement el) return;
            if (el.LocalName == "a")
            {
                el.SetAttribute("target", "_blank");
                el.SetAttribute("rel", "noopener noreferrer nofollow");
            }
            else if (el.LocalName == "img")
            {
                // Only our own images: no hotlinking, no third-party tracking pixels.
                var src = el.GetAttribute("src") ?? "";
                if (!src.StartsWith(_mediaBase, StringComparison.Ordinal)) el.Remove();
                else el.SetAttribute("loading", "lazy");
            }
        };
        _sanitizer.AllowedAttributes.Add("target");
        _sanitizer.AllowedAttributes.Add("rel");
        _sanitizer.AllowedAttributes.Add("loading");
    }

    public string Render(string markdown) =>
        _sanitizer.Sanitize(Markdown.ToHtml(markdown ?? "", Pipeline));
}
