namespace ReelGuo.Data.Entities;

/// <summary>
/// A build of the native app (Phase 6), published from Admin → App 版本. The desktop app's updater and the Android app's
/// update prompt read the newest published release for their platform.
/// </summary>
public sealed class AppRelease
{
    public int Id { get; set; }
    /// <summary>
    /// Updater target: windows-x86_64, darwin-aarch64, darwin-x86_64, linux-x86_64 (as the Tauri updater names them), or
    /// android.
    /// </summary>
    public required string Platform { get; set; }
    /// <summary>Semantic version, e.g. 0.6.0.</summary>
    public required string Version { get; set; }
    /// <summary>Download URL: the update bundle for desktop, the APK for Android.</summary>
    public required string Url { get; set; }
    /// <summary>Desktop only: the updater signature (content of the .sig file made with the release signing key).</summary>
    public string? Signature { get; set; }
    /// <summary>SHA-256 of the file, shown on the download page so people can check what they installed.</summary>
    public string? Sha256 { get; set; }
    public string Notes { get; set; } = "";
    /// <summary>Installs older than this release must update before they can be used.</summary>
    public bool Mandatory { get; set; }
    public bool Published { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public required string UpdatedBy { get; set; }
}
