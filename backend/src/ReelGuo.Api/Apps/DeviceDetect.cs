using System.Text.RegularExpressions;

namespace ReelGuo.Api.Apps;

/// <summary>The system a download request came from.</summary>
public enum DeviceOs
{
    Other,
    Android,
    /// <summary>iPhone, iPad, iPod. iPads asking for the desktop site say "Macintosh"; only the page itself can tell those apart.</summary>
    Ios,
    Windows,
    Mac,
    Linux,
    /// <summary>HarmonyOS NEXT (Huawei): not Android underneath, so it can't install APKs. Older HarmonyOS says "Android" and can.</summary>
    Harmony,
}

/// <param name="InApp">
/// A chat or social app's built-in browser (WeChat, QQ, Weibo, …). Those block file downloads, so visitors are asked to
/// open the page in a real browser first.
/// </param>
public sealed record Device(DeviceOs Os, bool InApp);

/// <summary>
/// Reads the device from a User-Agent header. The download page does the same in the browser
/// (web/apps/client/src/pages/device.ts); keep the two in step.
/// </summary>
public static partial class DeviceDetect
{
    public static Device FromUserAgent(string? userAgent)
    {
        var ua = userAgent ?? "";
        var os = OpenHarmony().IsMatch(ua) ? DeviceOs.Harmony
            : Android().IsMatch(ua) ? DeviceOs.Android
            : AppleMobile().IsMatch(ua) ? DeviceOs.Ios
            : Windows().IsMatch(ua) ? DeviceOs.Windows
            : Mac().IsMatch(ua) ? DeviceOs.Mac
            : Linux().IsMatch(ua) && !ChromeOs().IsMatch(ua) ? DeviceOs.Linux
            : DeviceOs.Other;
        return new Device(os, InAppBrowser().IsMatch(ua));
    }

    [GeneratedRegex(@"OpenHarmony|ArkWeb", RegexOptions.IgnoreCase)]
    private static partial Regex OpenHarmony();

    [GeneratedRegex(@"\bAndroid\b", RegexOptions.IgnoreCase)]
    private static partial Regex Android();

    [GeneratedRegex(@"\b(iPhone|iPad|iPod)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AppleMobile();

    [GeneratedRegex(@"Windows NT", RegexOptions.IgnoreCase)]
    private static partial Regex Windows();

    [GeneratedRegex(@"Macintosh|Mac OS X", RegexOptions.IgnoreCase)]
    private static partial Regex Mac();

    [GeneratedRegex(@"\bLinux\b|\bX11\b", RegexOptions.IgnoreCase)]
    private static partial Regex Linux();

    [GeneratedRegex(@"\bCrOS\b")]
    private static partial Regex ChromeOs();

    // WeChat, QQ (the chat app; QQBrowser is a real browser), Weibo, Alipay, DingTalk, Douyin / TikTok, Kuaishou,
    // Facebook, Instagram, LINE.
    [GeneratedRegex(@"MicroMessenger|\bQQ/|Weibo|AlipayClient|DingTalk|aweme|BytedanceWebview|musical_ly|Kwai|FBAN|FBAV|Instagram|\bLine/", RegexOptions.IgnoreCase)]
    private static partial Regex InAppBrowser();
}
