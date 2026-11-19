namespace ReelGuo.Api;

/// <summary>
/// The site's name as visitors see it (web/apps/client/src/brand.ts has the same values). "ReelGuo" stays where
/// software reads it, not people: <c>app_name</c> in /api/v1/config (the desktop and Android apps check it before
/// trusting a server), the <c>ReelGuoApp/</c> user agent, headers and code names.
/// </summary>
public static class Brand
{
    public const string Name = "黄月短剧";

    /// <summary>Short form inside compound labels: 黄月原创, 黄月APP.</summary>
    public const string Short = "黄月";
}
