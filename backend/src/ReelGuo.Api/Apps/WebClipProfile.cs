using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Options;
using ReelGuo.Api.Public;

namespace ReelGuo.Api.Apps;

/// <summary>
/// The iPhone "download": a configuration profile (.mobileconfig) holding one Web Clip, which puts the site on the home
/// screen as a full-screen app with our icon. iPhones can't install app files from a website, and this needs no Apple
/// account. Safari downloads it, then 设置 → 已下载描述文件 → 安装.
/// <para>
/// Every profile has the same identifier, so installing it again from another of our domains replaces the old icon
/// instead of adding a second one. Signed when <c>App:ProfileCertificatePath</c> is set (iOS then shows 已验证 instead of
/// 未签名); see docs/RUNBOOK.md §9.
/// </para>
/// </summary>
public sealed class WebClipProfile(IOptions<AppOptions> options, ILogger<WebClipProfile> log)
{
    public const string ContentType = "application/x-apple-aspen-config";
    public const string Identifier = "com.reelguo.webclip";

    private static readonly Lazy<byte[]> Icon = new(() =>
    {
        using var stream = typeof(WebClipProfile).Assembly.GetManifestResourceStream("apps/webclip-icon.png")
            ?? throw new InvalidOperationException("The web clip icon is missing from the build");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    });

    private readonly Lock _gate = new();
    private (string Path, DateTime Written, X509Certificate2 Cert, X509Certificate2Collection Chain)? _signing;

    /// <summary>The profile for the site at <paramref name="origin"/> (e.g. https://huangyueai.com), signed when configured.</summary>
    public byte[] Build(string origin)
    {
        var plist = Encoding.UTF8.GetBytes(Plist(origin, Icon.Value));
        return Sign(plist) ?? plist;
    }

    internal static string Plist(string origin, byte[] icon)
    {
        static string S(string value) => SecurityElement.Escape(value);
        var url = origin.TrimEnd('/') + "/";
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
            	<key>PayloadContent</key>
            	<array>
            		<dict>
            			<key>FullScreen</key>
            			<true/>
            			<key>Icon</key>
            			<data>{Convert.ToBase64String(icon)}</data>
            			<key>IgnoreManifestScope</key>
            			<true/>
            			<key>IsRemovable</key>
            			<true/>
            			<key>Label</key>
            			<string>{S(Brand.Name)}</string>
            			<key>PayloadDescription</key>
            			<string>{S($"在主屏幕添加「{Brand.Name}」")}</string>
            			<key>PayloadDisplayName</key>
            			<string>{S(Brand.Name)}</string>
            			<key>PayloadIdentifier</key>
            			<string>{Identifier}.clip</string>
            			<key>PayloadType</key>
            			<string>com.apple.webClip.managed</string>
            			<key>PayloadUUID</key>
            			<string>{Uuid(url, "clip")}</string>
            			<key>PayloadVersion</key>
            			<integer>1</integer>
            			<key>Precomposed</key>
            			<true/>
            			<key>URL</key>
            			<string>{S(url)}</string>
            		</dict>
            	</array>
            	<key>PayloadDescription</key>
            	<string>{S($"安装后主屏幕会出现「{Brand.Name}」图标，点开即可全屏观看。本描述文件只添加这一个图标，不读取手机里的任何数据，可随时在 设置 → 通用 → VPN与设备管理 中删除。仅限 18 岁以上成年人使用。")}</string>
            	<key>PayloadDisplayName</key>
            	<string>{S(Brand.Name)}</string>
            	<key>PayloadIdentifier</key>
            	<string>{Identifier}</string>
            	<key>PayloadOrganization</key>
            	<string>{S(Brand.Name)}</string>
            	<key>PayloadRemovalDisallowed</key>
            	<false/>
            	<key>PayloadType</key>
            	<string>Configuration</string>
            	<key>PayloadUUID</key>
            	<string>{Uuid(url, "profile")}</string>
            	<key>PayloadVersion</key>
            	<integer>1</integer>
            </dict>
            </plist>

            """;
    }

    /// <summary>A stable UUID per site address, so the same profile downloaded twice is the same profile.</summary>
    private static string Uuid(string url, string part)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{Identifier}|{part}|{url}"));
        hash[6] = (byte)(hash[6] & 0x0F | 0x50);   // version 5 (name-based)
        hash[8] = (byte)(hash[8] & 0x3F | 0x80);   // RFC 4122 variant
        return new Guid(hash.AsSpan(0, 16), bigEndian: true).ToString().ToUpperInvariant();
    }

    /// <summary>CMS-signs the profile with the configured certificate, or returns null when none is set or it can't be used.</summary>
    private byte[]? Sign(byte[] plist)
    {
        var path = options.Value.ProfileCertificatePath;
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var (cert, chain) = Certificate(path, options.Value.ProfileCertificatePassword);
            var cms = new SignedCms(new ContentInfo(plist), detached: false);
            var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, cert) { IncludeOption = X509IncludeOption.EndCertOnly };
            signer.Certificates.AddRange(chain);
            cms.ComputeSignature(signer, silent: true);
            return cms.Encode();
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log.LogWarning(ex, "Could not sign the iPhone profile with {Path}; serving it unsigned", path);
            return null;
        }
    }

    /// <summary>The signing certificate (a .pfx / .p12 with its private key and intermediates), re-read when the file changes.</summary>
    private (X509Certificate2 Cert, X509Certificate2Collection Chain) Certificate(string path, string? password)
    {
        var written = File.GetLastWriteTimeUtc(path);
        lock (_gate)
        {
            if (_signing is { } s && s.Path == path && s.Written == written) return (s.Cert, s.Chain);
            var all = X509CertificateLoader.LoadPkcs12CollectionFromFile(path, password);
            var cert = all.FirstOrDefault(c => c.HasPrivateKey) ?? throw new InvalidOperationException("The certificate file has no private key");
            var chain = new X509Certificate2Collection();
            foreach (var c in all)
                if (!c.HasPrivateKey && !c.SubjectName.RawData.AsSpan().SequenceEqual(c.IssuerName.RawData)) chain.Add(c);   // intermediates, not roots
            _signing = (path, written, cert, chain);
            return (cert, chain);
        }
    }
}
